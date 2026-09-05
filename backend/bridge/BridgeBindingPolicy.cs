using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace CodexLanBridge;

internal sealed record BridgeBindingDecision(
    bool AdministratorMode,
    IReadOnlyList<string> Urls,
    IReadOnlyList<IPAddress> TailscaleAddresses)
{
    internal string UrlSetting => string.Join(';', Urls);
}

internal sealed record BridgeNetworkAddress(
    string Name,
    string Description,
    bool IsUp,
    IPAddress Address);

internal static class BridgeBindingPolicy
{
    internal const int Port = 8787;

    internal static BridgeBindingDecision ResolveCurrent(bool administratorMode, string? configuredUrls)
    {
        return Resolve(administratorMode, configuredUrls, ReadNetworkAddresses());
    }

    private static IEnumerable<BridgeNetworkAddress> ReadNetworkAddresses()
    {
        NetworkInterface[] networks;
        try { networks = NetworkInterface.GetAllNetworkInterfaces(); }
        catch { return Array.Empty<BridgeNetworkAddress>(); }
        return networks
            .SelectMany(network =>
            {
                IEnumerable<UnicastIPAddressInformation> unicast;
                try { unicast = network.GetIPProperties().UnicastAddresses; }
                catch { unicast = Array.Empty<UnicastIPAddressInformation>(); }
                return unicast.Select(address => new BridgeNetworkAddress(
                    network.Name,
                    network.Description,
                    network.OperationalStatus == OperationalStatus.Up,
                    address.Address));
            })
            .ToArray();
    }

    internal static BridgeBindingDecision Resolve(
        bool administratorMode,
        string? configuredUrls,
        IEnumerable<BridgeNetworkAddress> addresses)
    {
        if (!administratorMode)
        {
            var setting = string.IsNullOrWhiteSpace(configuredUrls)
                ? $"http://0.0.0.0:{Port}"
                : configuredUrls;
            var standardUrls = setting.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return new(false, standardUrls.Length == 0 ? [$"http://0.0.0.0:{Port}"] : standardUrls, []);
        }

        // An elevated HTTP process must never be reachable from an ordinary LAN.
        // Configuration and environment URL overrides are intentionally ignored.
        var tailscale = addresses
            .Where(IsTailscaleIpv4)
            .Select(candidate => candidate.Address)
            .Distinct()
            .OrderBy(address => address.ToString(), StringComparer.Ordinal)
            .ToArray();
        if (tailscale.Length == 0)
        {
            throw new InvalidOperationException(
                "Administrator Mode requires an active Tailscale IPv4 address. " +
                "Startup will retry instead of silently becoming loopback-only.");
        }
        var elevatedUrls = new List<string> { $"http://127.0.0.1:{Port}" };
        elevatedUrls.AddRange(tailscale.Select(address => $"http://{address}:{Port}"));
        return new(true, elevatedUrls, tailscale);
    }

    private static bool IsTailscaleIpv4(BridgeNetworkAddress candidate)
    {
        if (!candidate.IsUp || candidate.Address.AddressFamily != AddressFamily.InterNetwork ||
            IPAddress.IsLoopback(candidate.Address)) return false;
        var bytes = candidate.Address.GetAddressBytes();
        var isTailscaleIpv4Range = bytes.Length == 4 && bytes[0] == 100 && bytes[1] is >= 64 and <= 127;
        return isTailscaleIpv4Range &&
               (candidate.Name.Contains("Tailscale", StringComparison.OrdinalIgnoreCase) ||
                candidate.Description.Contains("Tailscale", StringComparison.OrdinalIgnoreCase));
    }
}
