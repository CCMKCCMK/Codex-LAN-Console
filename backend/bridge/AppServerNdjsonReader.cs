using System.Buffers;
using System.IO.Pipelines;
using System.Text.Json;

namespace CodexLanBridge;

public sealed record AppServerOversizedMessage(
    long ActualBytes,
    long? NumericId,
    string? ServerMethod,
    bool EndedWithNewline);

/// <summary>
/// Reads the app-server newline-delimited protocol without retaining an
/// unbounded line. An oversized line is drained in-place so the next JSON-RPC
/// response can still be routed over the same process and stream.
/// </summary>
public static class AppServerNdjsonReader
{
    private const int HeaderCaptureBytes = 16 * 1024;

    public static async Task ReadAsync(
        Stream stream,
        long maximumMessageBytes,
        Func<ReadOnlySequence<byte>, Task> onMessage,
        Func<AppServerOversizedMessage, Task> onOversized,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(onMessage);
        ArgumentNullException.ThrowIfNull(onOversized);
        if (maximumMessageBytes < 1) throw new ArgumentOutOfRangeException(nameof(maximumMessageBytes));

        var reader = PipeReader.Create(
            stream,
            new StreamPipeReaderOptions(
                bufferSize: 64 * 1024,
                minimumReadSize: 4 * 1024,
                leaveOpen: true));
        try
        {
            await ReadAsync(reader, maximumMessageBytes, onMessage, onOversized, cancellationToken);
        }
        finally
        {
            await reader.CompleteAsync();
        }
    }

    internal static async Task ReadAsync(
        PipeReader reader,
        long maximumMessageBytes,
        Func<ReadOnlySequence<byte>, Task> onMessage,
        Func<AppServerOversizedMessage, Task> onOversized,
        CancellationToken cancellationToken)
    {
        var discarding = false;
        var oversizedBytes = 0L;
        var header = new ArrayBufferWriter<byte>(HeaderCaptureBytes);

        while (!cancellationToken.IsCancellationRequested)
        {
            var read = await reader.ReadAsync(cancellationToken);
            var remaining = read.Buffer;

            while (true)
            {
                if (discarding)
                {
                    if (remaining.PositionOf((byte)'\n') is { } discardedNewline)
                    {
                        var discarded = remaining.Slice(0, discardedNewline);
                        CaptureHeader(discarded, header);
                        oversizedBytes += discarded.Length;
                        remaining = remaining.Slice(remaining.GetPosition(1, discardedNewline));
                        await onOversized(DescribeOversized(
                            oversizedBytes,
                            header.WrittenSpan,
                            endedWithNewline: true));
                        discarding = false;
                        oversizedBytes = 0;
                        header.Clear();
                        continue;
                    }

                    CaptureHeader(remaining, header);
                    oversizedBytes += remaining.Length;
                    remaining = remaining.Slice(remaining.End);
                    break;
                }

                if (remaining.PositionOf((byte)'\n') is { } newline)
                {
                    var line = remaining.Slice(0, newline);
                    remaining = remaining.Slice(remaining.GetPosition(1, newline));
                    if (line.Length > maximumMessageBytes)
                    {
                        header.Clear();
                        CaptureHeader(line, header);
                        await onOversized(DescribeOversized(
                            line.Length,
                            header.WrittenSpan,
                            endedWithNewline: true));
                    }
                    else if (line.Length > 0)
                    {
                        await onMessage(TrimCarriageReturn(line));
                    }
                    continue;
                }

                if (remaining.Length > maximumMessageBytes)
                {
                    discarding = true;
                    oversizedBytes = remaining.Length;
                    header.Clear();
                    CaptureHeader(remaining, header);
                    // Consume the bytes already known to belong to the bad line.
                    // Keeping them examined-but-unconsumed would let PipeReader
                    // grow until the producer finally emits a newline.
                    remaining = remaining.Slice(remaining.End);
                }
                break;
            }

            if (read.IsCompleted)
            {
                if (discarding)
                {
                    // The current buffer was consumed above. Report the unterminated
                    // line as oversized rather than turning EOF into a global parse
                    // or transport failure.
                    await onOversized(DescribeOversized(
                        oversizedBytes,
                        header.WrittenSpan,
                        endedWithNewline: false));
                    discarding = false;
                    oversizedBytes = 0;
                    header.Clear();
                }
                else if (remaining.Length > 0)
                {
                    if (remaining.Length > maximumMessageBytes)
                    {
                        header.Clear();
                        CaptureHeader(remaining, header);
                        await onOversized(DescribeOversized(
                            remaining.Length,
                            header.WrittenSpan,
                            endedWithNewline: false));
                    }
                    else
                    {
                        await onMessage(TrimCarriageReturn(remaining));
                    }
                    remaining = remaining.Slice(remaining.End);
                }
            }

            reader.AdvanceTo(remaining.Start, remaining.End);
            if (read.IsCompleted) break;
        }
    }

    private static void CaptureHeader(
        ReadOnlySequence<byte> source,
        ArrayBufferWriter<byte> destination)
    {
        var available = HeaderCaptureBytes - destination.WrittenCount;
        if (available <= 0 || source.Length == 0) return;
        var length = (int)Math.Min(source.Length, available);
        source.Slice(0, length).CopyTo(destination.GetSpan(length));
        destination.Advance(length);
    }

    private static AppServerOversizedMessage DescribeOversized(
        long actualBytes,
        ReadOnlySpan<byte> header,
        bool endedWithNewline)
    {
        long? numericId = null;
        string? serverMethod = null;
        try
        {
            var json = new Utf8JsonReader(header, isFinalBlock: false, state: default);
            while (json.Read())
            {
                if (json.TokenType != JsonTokenType.PropertyName || json.CurrentDepth != 1) continue;
                if (json.ValueTextEquals("id"u8))
                {
                    if (json.Read() && json.TokenType == JsonTokenType.Number && json.TryGetInt64(out var id))
                        numericId = id;
                    continue;
                }
                if (json.ValueTextEquals("method"u8))
                {
                    if (json.Read() && json.TokenType == JsonTokenType.String)
                        serverMethod = Limit(json.GetString(), 256);
                }
            }
        }
        catch (JsonException)
        {
            // The prefix is intentionally incomplete. Metadata is best effort;
            // bounded draining and stream recovery do not depend on it.
        }
        return new AppServerOversizedMessage(actualBytes, numericId, serverMethod, endedWithNewline);
    }

    private static ReadOnlySequence<byte> TrimCarriageReturn(ReadOnlySequence<byte> line)
    {
        if (line.Length == 0) return line;
        var last = line.Slice(line.Length - 1, 1).FirstSpan[0];
        return last == (byte)'\r' ? line.Slice(0, line.Length - 1) : line;
    }

    private static string? Limit(string? value, int maximumCharacters) =>
        string.IsNullOrEmpty(value)
            ? null
            : value.Length <= maximumCharacters ? value : value[..maximumCharacters];
}
