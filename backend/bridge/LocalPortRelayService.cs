using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace CodexLanBridge;

public sealed record LocalLinkResolution(string Url, string Mode, int TargetPort, int PublicPort, DateTimeOffset ExpiresAt);

public sealed class LocalPortRelayService : BackgroundService
{
    private static readonly TimeSpan LeaseLifetime = TimeSpan.FromMinutes(10);
    private static readonly Regex LocalhostCookieDomain = new(
        ";\\s*Domain\\s*=\\s*\\.?(?:localhost|[^;=]+\\.localhost)(?=;|$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private readonly ConcurrentDictionary<string, RelayBinding> _bindings = new(StringComparer.Ordinal);
    private readonly PairingService _pairing;

    public LocalPortRelayService(PairingService pairing) => _pairing = pairing;

    public static bool IsLocalDevelopmentUrl(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.UserInfo.Length > 0 || uri.Scheme is not ("http" or "https")) return false;
        var host = uri.DnsSafeHost.TrimEnd('.');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
            host is "0.0.0.0" or "::") return true;
        return IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
    }

    public async Task<LocalLinkResolution> ResolveAsync(
        Uri target,
        IPAddress listenAddress,
        IPAddress clientAddress,
        string publicHost,
        int bridgePort,
        CancellationToken cancellationToken)
    {
        if (!IsLocalDevelopmentUrl(target)) throw new ArgumentException("Only localhost HTTP links can be mapped.");
        if (!target.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Local HTTPS links cannot be safely remapped because their certificate is issued for localhost.");
        if (IPAddress.IsLoopback(listenAddress) || listenAddress.Equals(IPAddress.Any) || listenAddress.Equals(IPAddress.IPv6Any))
            throw new InvalidOperationException("Open the console through the computer's LAN or Tailscale address before mapping a local link.");

        var targetPort = target.IsDefaultPort ? 80 : target.Port;
        if (targetPort is < 1 or > 65535 || targetPort == bridgePort)
            throw new ArgumentException("This local port cannot be mapped.");
        if (!await CanConnectAsync(IPAddress.Loopback, targetPort, cancellationToken) &&
            !await CanConnectAsync(IPAddress.IPv6Loopback, targetPort, cancellationToken))
            throw new InvalidOperationException($"Nothing is listening on localhost:{targetPort} on the remote computer.");

        var expiresAt = DateTimeOffset.UtcNow.Add(LeaseLifetime);
        var key = $"{listenAddress}|{targetPort}";
        RelayBinding binding;
        while (true)
        {
            if (_bindings.TryGetValue(key, out binding!) && !binding.IsDisposed) break;
            if (binding is not null && binding.IsDisposed) RemoveBinding(key, binding);

            var candidate = CreateBinding(key, listenAddress, targetPort, preferredPort: targetPort, publicHost, bridgePort);
            if (_bindings.TryAdd(key, candidate))
            {
                binding = candidate;
                binding.Start(AcceptLoopAsync);
                break;
            }
            candidate.Dispose();
        }
        if (!binding.TryGrant(clientAddress, expiresAt))
        {
            RemoveBinding(key, binding);
            return await ResolveAsync(target, listenAddress, clientAddress, publicHost, bridgePort, cancellationToken);
        }
        return new(BuildPublicUrl(publicHost, binding.PublicPort, target), "relay", targetPort, binding.PublicPort, expiresAt);
    }

    private RelayBinding CreateBinding(string key, IPAddress listenAddress, int targetPort, int preferredPort, string publicHost, int bridgePort)
    {
        try
        {
            return new RelayBinding(key, listenAddress, targetPort, preferredPort, publicHost, bridgePort);
        }
        catch (SocketException)
        {
            return new RelayBinding(key, listenAddress, targetPort, 0, publicHost, bridgePort);
        }
    }

    private bool RemoveBinding(string key, RelayBinding binding) =>
        ((ICollection<KeyValuePair<string, RelayBinding>>)_bindings).Remove(new(key, binding));

    private async Task AcceptLoopAsync(RelayBinding binding)
    {
        try
        {
            while (!binding.Token.IsCancellationRequested)
            {
                var client = await binding.Listener.AcceptTcpClientAsync(binding.Token);
                _ = HandleClientSafelyAsync(binding, client);
            }
        }
        catch (OperationCanceledException) when (binding.Token.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (binding.Token.IsCancellationRequested) { }
        catch (Exception ex) { Console.Error.WriteLine($"Local port relay {binding.PublicPort} stopped: {ex.Message}"); }
        finally
        {
            RemoveBinding(binding.Key, binding);
            binding.Dispose();
        }
    }

    private async Task HandleClientSafelyAsync(RelayBinding binding, TcpClient client)
    {
        try { await HandleClientAsync(binding, client); }
        catch (OperationCanceledException) when (binding.Token.IsCancellationRequested) { }
        catch (OperationCanceledException) { client.Dispose(); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Local port relay {binding.PublicPort} request failed: {ex.Message}");
            client.Dispose();
        }
    }

    private async Task HandleClientAsync(RelayBinding binding, TcpClient client)
    {
        using (client)
        {
            client.NoDelay = true;
            var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
            if (remote?.IsIPv4MappedToIPv6 == true) remote = remote.MapToIPv4();
            if (remote is null || !binding.TryGetGrant(remote, out var grantExpiresAt))
            {
                await WriteErrorAsync(client, 403, "This phone does not have an active port lease.");
                return;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(binding.Token);
            var leaseRemaining = grantExpiresAt - DateTimeOffset.UtcNow;
            if (leaseRemaining <= TimeSpan.Zero)
            {
                await WriteErrorAsync(client, 403, "This phone's port lease has expired.");
                return;
            }
            timeout.CancelAfter(leaseRemaining);
            using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            headerTimeout.CancelAfter(TimeSpan.FromSeconds(30));
            var downstream = client.GetStream();
            var request = await ReadHeaderAsync(downstream, headerTimeout.Token);
            if (request is null || !TryAuthenticate(request.Header, out var cleanHeader))
            {
                await WriteErrorAsync(client, 401, "Pair this device before opening remote localhost links.");
                return;
            }

            using var upstream = await ConnectLoopbackAsync(binding.TargetPort, headerTimeout.Token);
            var upstreamStream = upstream.GetStream();
            var rewritten = RewriteRequest(cleanHeader, binding, out var isUpgrade);
            await upstreamStream.WriteAsync(rewritten, headerTimeout.Token);
            if (request.Remainder.Length > 0) await upstreamStream.WriteAsync(request.Remainder, headerTimeout.Token);
            await upstreamStream.FlushAsync(headerTimeout.Token);

            headerTimeout.CancelAfter(Timeout.InfiniteTimeSpan);
            var upload = downstream.CopyToAsync(upstreamStream, timeout.Token);
            var download = CopyResponseAsync(upstreamStream, downstream, binding, remote, isUpgrade, timeout.Token);
            var first = await Task.WhenAny(upload, download);
            if (ReferenceEquals(first, upload) && !download.IsCompleted)
            {
                try { upstream.Client.Shutdown(SocketShutdown.Send); } catch { }
                await download;
            }
            else
            {
                await first;
            }
        }
    }

    private bool TryAuthenticate(byte[] rawHeader, out byte[] cleanHeader)
    {
        var text = Encoding.Latin1.GetString(rawHeader);
        var lines = text.Split("\r\n", StringSplitOptions.None);
        var authenticated = false;
        var output = new List<string>(lines.Length);
        foreach (var line in lines)
        {
            if (line.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase))
            {
                var value = line[(line.IndexOf(':') + 1)..].Trim();
                if (value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) && _pairing.Validate(value[7..].Trim()))
                {
                    authenticated = true;
                    continue;
                }
                output.Add(line);
                continue;
            }
            if (line.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase))
            {
                var cookies = line[(line.IndexOf(':') + 1)..].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var kept = new List<string>();
                foreach (var cookie in cookies)
                {
                    var split = cookie.IndexOf('=');
                    if (split > 0 && cookie[..split].Trim().Equals(PairingService.SessionCookieName, StringComparison.Ordinal))
                    {
                        var encodedToken = cookie[(split + 1)..].Trim();
                        string token;
                        try { token = Uri.UnescapeDataString(encodedToken); }
                        catch (UriFormatException) { token = ""; }
                        authenticated |= _pairing.Validate(token);
                    }
                    else kept.Add(cookie);
                }
                if (kept.Count > 0) output.Add("Cookie: " + string.Join("; ", kept));
                continue;
            }
            output.Add(line);
        }
        cleanHeader = Encoding.Latin1.GetBytes(string.Join("\r\n", output));
        return authenticated;
    }

    private static byte[] RewriteRequest(byte[] rawHeader, RelayBinding binding, out bool isUpgrade)
    {
        var source = Encoding.Latin1.GetString(rawHeader).Split("\r\n", StringSplitOptions.None);
        isUpgrade = source.Any(line => line.StartsWith("Upgrade:", StringComparison.OrdinalIgnoreCase)) &&
                    source.Any(line => line.StartsWith("Connection:", StringComparison.OrdinalIgnoreCase) &&
                                       line[(line.IndexOf(':') + 1)..].Split(',').Any(token => token.Trim().Equals("upgrade", StringComparison.OrdinalIgnoreCase)));
        var lines = new List<string>(source.Length + 1);
        var hasConnection = false;
        foreach (var sourceLine in source)
        {
            var line = sourceLine;
            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                lines.Add(line);
                continue;
            }
            var name = line[..separator];
            var value = line[(separator + 1)..].Trim();
            if (name.Equals("Proxy-Connection", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                lines.Add($"Host: 127.0.0.1:{binding.TargetPort}");
                continue;
            }
            if (name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
            {
                hasConnection = true;
                lines.Add(isUpgrade ? line : "Connection: close");
                continue;
            }
            if ((!name.Equals("Origin", StringComparison.OrdinalIgnoreCase) &&
                 !name.Equals("Referer", StringComparison.OrdinalIgnoreCase)) ||
                !Uri.TryCreate(value, UriKind.Absolute, out var origin))
            {
                lines.Add(line);
                continue;
            }
            if (!IsLocalDevelopmentUrl(origin) && !IsRelayOrigin(origin, binding))
            {
                lines.Add(line);
                continue;
            }
            var replacement = new UriBuilder("http", "127.0.0.1", binding.TargetPort, origin.AbsolutePath)
            {
                Query = origin.Query.TrimStart('?'),
                Fragment = origin.Fragment.TrimStart('#')
            }.Uri.AbsoluteUri;
            lines.Add($"{name}: {(name.Equals("Origin", StringComparison.OrdinalIgnoreCase) ? $"http://127.0.0.1:{binding.TargetPort}" : replacement)}");
        }
        if (!isUpgrade && !hasConnection) lines.Insert(HeaderEndIndex(lines), "Connection: close");
        return Encoding.Latin1.GetBytes(string.Join("\r\n", lines));
    }

    private async Task CopyResponseAsync(
        Stream upstream,
        Stream downstream,
        RelayBinding binding,
        IPAddress clientAddress,
        bool isUpgrade,
        CancellationToken cancellationToken)
    {
        var response = await ReadHeaderAsync(upstream, cancellationToken);
        if (response is null) return;
        var rewritten = await RewriteResponseAsync(response.Header, binding, clientAddress, isUpgrade, cancellationToken);
        await downstream.WriteAsync(rewritten, cancellationToken);
        if (response.Remainder.Length > 0) await downstream.WriteAsync(response.Remainder, cancellationToken);
        await upstream.CopyToAsync(downstream, cancellationToken);
    }

    private async Task<byte[]> RewriteResponseAsync(
        byte[] rawHeader,
        RelayBinding binding,
        IPAddress clientAddress,
        bool isUpgrade,
        CancellationToken cancellationToken)
    {
        var lines = Encoding.Latin1.GetString(rawHeader).Split("\r\n", StringSplitOptions.None);
        var output = new List<string>(lines.Length + 1);
        var publicBase = $"http://{FormatHost(binding.PublicHost)}:{binding.PublicPort}";
        var hasConnection = false;
        foreach (var line in lines)
        {
            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                output.Add(line);
                continue;
            }
            var name = line[..separator];
            var value = line[(separator + 1)..].Trim();
            if (name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
            {
                hasConnection = true;
                output.Add(isUpgrade ? line : "Connection: close");
                continue;
            }
            if (name.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase))
            {
                var cookieNameEnd = value.IndexOf('=');
                if (cookieNameEnd > 0 && value[..cookieNameEnd].Trim().Equals(PairingService.SessionCookieName, StringComparison.Ordinal))
                    continue;
                output.Add($"{name}: {LocalhostCookieDomain.Replace(value, "")}");
                continue;
            }
            if (name.Equals("Location", StringComparison.OrdinalIgnoreCase) &&
                Uri.TryCreate(value, UriKind.Absolute, out var location) && IsLocalDevelopmentUrl(location))
            {
                try
                {
                    LocalLinkResolution resolution;
                    var locationPort = location.IsDefaultPort ? (location.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? 443 : 80) : location.Port;
                    if (location.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) && locationPort == binding.TargetPort)
                        resolution = new(BuildPublicUrl(binding.PublicHost, binding.PublicPort, location), "relay", binding.TargetPort, binding.PublicPort, DateTimeOffset.UtcNow.Add(LeaseLifetime));
                    else
                        resolution = await ResolveAsync(location, binding.ListenAddress, clientAddress, binding.PublicHost, binding.BridgePort, cancellationToken);
                    output.Add($"{name}: {resolution.Url}");
                    continue;
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException or SocketException)
                {
                    // Leave an unsupported redirect unchanged; the client can show the original target instead of receiving a broken URL.
                    Console.Error.WriteLine($"Could not map redirect {location}: {ex.Message}");
                }
            }
            if (name.Equals("Access-Control-Allow-Origin", StringComparison.OrdinalIgnoreCase) &&
                Uri.TryCreate(value, UriKind.Absolute, out var allowedOrigin) && IsLocalDevelopmentUrl(allowedOrigin))
            {
                output.Add($"{name}: {publicBase}");
                continue;
            }
            output.Add(line);
        }
        if (!isUpgrade && !hasConnection) output.Insert(HeaderEndIndex(output), "Connection: close");
        return Encoding.Latin1.GetBytes(string.Join("\r\n", output));
    }

    private static int HeaderEndIndex(IReadOnlyList<string> lines)
    {
        for (var index = 1; index < lines.Count; index++)
            if (lines[index].Length == 0) return index;
        return lines.Count;
    }

    private static bool IsRelayOrigin(Uri uri, RelayBinding binding)
    {
        if (!uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)) return false;
        var port = uri.IsDefaultPort ? 80 : uri.Port;
        if (port != binding.PublicPort) return false;
        if (uri.DnsSafeHost.Equals(binding.PublicHost, StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(uri.DnsSafeHost, out var address) && address.Equals(binding.ListenAddress);
    }

    private static async Task<HeaderPacket?> ReadHeaderAsync(Stream stream, CancellationToken cancellationToken)
    {
        const int maximum = 64 * 1024;
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        while (buffer.Length < maximum)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0) return null;
            buffer.Write(chunk, 0, read);
            var bytes = buffer.GetBuffer();
            var length = checked((int)buffer.Length);
            for (var index = Math.Max(0, length - read - 3); index <= length - 4; index++)
                if (bytes[index] == 13 && bytes[index + 1] == 10 && bytes[index + 2] == 13 && bytes[index + 3] == 10)
                {
                    var all = buffer.ToArray();
                    var headerLength = index + 4;
                    return new HeaderPacket(all[..headerLength], all[headerLength..]);
                }
        }
        return null;
    }

    private static async Task<TcpClient> ConnectLoopbackAsync(int port, CancellationToken cancellationToken)
    {
        foreach (var address in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
        {
            var client = new TcpClient(address.AddressFamily) { NoDelay = true };
            try
            {
                await client.ConnectAsync(address, port, cancellationToken);
                return client;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                client.Dispose();
                throw;
            }
            catch { client.Dispose(); }
        }
        throw new SocketException((int)SocketError.ConnectionRefused);
    }

    private static async Task<bool> CanConnectAsync(IPAddress address, int port, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(500));
        using var client = new TcpClient(address.AddressFamily);
        try { await client.ConnectAsync(address, port, timeout.Token); return true; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return false; }
    }

    private static async Task WriteErrorAsync(TcpClient client, int status, string message)
    {
        var body = Encoding.UTF8.GetBytes(message);
        var reason = status == 401 ? "Unauthorized" : "Forbidden";
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {reason}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        try
        {
            await client.GetStream().WriteAsync(header);
            await client.GetStream().WriteAsync(body);
        }
        catch { }
    }

    private static string BuildPublicUrl(string publicHost, int publicPort, Uri target)
    {
        var builder = new UriBuilder("http", publicHost, publicPort, target.AbsolutePath)
        {
            Query = target.Query.TrimStart('?'),
            Fragment = target.Fragment.TrimStart('#')
        };
        return builder.Uri.AbsoluteUri;
    }

    private static string FormatHost(string host) => host.Contains(':') && !host.StartsWith('[') ? $"[{host}]" : host;

    private sealed record HeaderPacket(byte[] Header, byte[] Remainder);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                foreach (var pair in _bindings)
                {
                    pair.Value.TryRetireIfUnused(() => RemoveBinding(pair.Key, pair.Value));
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public override void Dispose()
    {
        foreach (var binding in _bindings.Values) binding.Dispose();
        _bindings.Clear();
        base.Dispose();
    }

    private sealed class RelayBinding : IDisposable
    {
        private readonly ConcurrentDictionary<string, DateTimeOffset> _grants = new(StringComparer.Ordinal);
        private readonly CancellationTokenSource _stop = new();
        private readonly CancellationToken _token;
        private readonly object _lifetimeGate = new();
        private int _disposed;
        public string Key { get; }
        public TcpListener Listener { get; }
        public IPAddress ListenAddress { get; }
        public int BridgePort { get; }
        public int TargetPort { get; }
        public int PublicPort { get; }
        public string PublicHost { get; }
        public CancellationToken Token => _token;
        public bool HasGrants => !_grants.IsEmpty;
        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        public RelayBinding(string key, IPAddress listenAddress, int targetPort, int publicPort, string publicHost, int bridgePort)
        {
            Key = key;
            ListenAddress = listenAddress;
            BridgePort = bridgePort;
            TargetPort = targetPort;
            PublicHost = publicHost;
            _token = _stop.Token;
            Listener = new TcpListener(listenAddress, publicPort);
            Listener.Server.ExclusiveAddressUse = true;
            Listener.Start(32);
            PublicPort = ((IPEndPoint)Listener.LocalEndpoint).Port;
        }

        public void Start(Func<RelayBinding, Task> acceptLoop) => _ = Task.Run(() => acceptLoop(this));
        public bool TryGrant(IPAddress address, DateTimeOffset expiresAt)
        {
            lock (_lifetimeGate)
            {
                if (IsDisposed) return false;
                _grants[address.ToString()] = expiresAt;
                return true;
            }
        }
        public bool TryGetGrant(IPAddress address, out DateTimeOffset expiry) =>
            _grants.TryGetValue(address.ToString(), out expiry) && expiry > DateTimeOffset.UtcNow;
        public void TryRetireIfUnused(Func<bool> removeBinding)
        {
            lock (_lifetimeGate)
            {
                if (IsDisposed) return;
                foreach (var grant in _grants)
                    if (grant.Value <= DateTimeOffset.UtcNow) _grants.TryRemove(grant.Key, out _);
                if (!_grants.IsEmpty || !removeBinding()) return;
                DisposeCore();
            }
        }
        public void Dispose()
        {
            lock (_lifetimeGate) DisposeCore();
        }
        private void DisposeCore()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _stop.Cancel();
            try { Listener.Stop(); } catch { }
            _stop.Dispose();
        }
    }
}
