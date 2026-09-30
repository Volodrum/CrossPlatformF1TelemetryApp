using F1Telemetry.Protocol;

namespace F1Telemetry.Core;

/// <summary>Thread-safe ingest counters (written by the pipeline, read by the UI).</summary>
public sealed class PacketStatistics
{
    private readonly long[] _byPacketId = new long[32];
    private long _rejectedSize;
    private long _rejectedFormat;
    private long _rejectedOther;
    private long _rejectedMode;
    private long _total;

    public void Record(ParseResult result)
    {
        Interlocked.Increment(ref _total);
        switch (result.Status)
        {
            case ParseStatus.Ok:
                var id = (int)result.Header!.Value.PacketId;
                if (id < _byPacketId.Length)
                {
                    Interlocked.Increment(ref _byPacketId[id]);
                }

                LastFormat = result.Header.Value.Format;
                break;
            case ParseStatus.SizeMismatch:
                Interlocked.Increment(ref _rejectedSize);
                break;
            case ParseStatus.UnsupportedFormat:
                Interlocked.Increment(ref _rejectedFormat);
                LastUnsupportedFormat = result.Header?.PacketFormat;
                break;
            default:
                Interlocked.Increment(ref _rejectedOther);
                break;
        }
    }

    public long Total => Interlocked.Read(ref _total);
    public long RejectedSize => Interlocked.Read(ref _rejectedSize);
    public long RejectedFormat => Interlocked.Read(ref _rejectedFormat);
    public long RejectedOther => Interlocked.Read(ref _rejectedOther);

    /// <summary>Valid packets dropped because they belong to the other telemetry mode (F1 25 vs F1 26).</summary>
    public long RejectedMode => Interlocked.Read(ref _rejectedMode);

    public GameFormat? LastMismatchedFormat { get; private set; }

    public void RecordModeMismatch(GameFormat format)
    {
        Interlocked.Increment(ref _rejectedMode);
        LastMismatchedFormat = format;
    }

    public GameFormat? LastFormat { get; private set; }
    public ushort? LastUnsupportedFormat { get; private set; }

    public IReadOnlyDictionary<PacketId, long> Snapshot()
    {
        var result = new Dictionary<PacketId, long>();
        for (var i = 0; i < _byPacketId.Length; i++)
        {
            var count = Interlocked.Read(ref _byPacketId[i]);
            if (count > 0)
            {
                result[(PacketId)i] = count;
            }
        }

        return result;
    }
}
