using F1Telemetry.Protocol.Packets;

namespace F1Telemetry.Protocol;

public enum ParseStatus
{
    Ok,
    TooShort,
    UnsupportedFormat,
    UnknownPacketId,
    SizeMismatch,
}

public readonly record struct ParseResult(ParseStatus Status, Packet? Packet, PacketHeader? Header, int ExpectedSize = 0)
{
    public bool IsSuccess => Status == ParseStatus.Ok;
}

/// <summary>
/// Stateless entry point: raw datagram in, typed packet out. Packet size is validated exactly against the
/// selected format so a format mismatch (e.g. game set to 2026 but data misread as 2025) can never
/// silently produce garbage values.
/// </summary>
public static class PacketParser
{
    public static ParseResult Parse(ReadOnlySpan<byte> datagram)
    {
        if (datagram.Length < FormatLayout.HeaderSize)
        {
            return new ParseResult(ParseStatus.TooShort, null, null);
        }

        var reader = new SpanReader(datagram);
        var header = PacketHeader.Read(ref reader);

        if (!FormatLayout.TryGet(header.PacketFormat, out var layout))
        {
            return new ParseResult(ParseStatus.UnsupportedFormat, null, header);
        }

        if (!layout.PacketSizes.TryGetValue(header.PacketId, out var expectedSize))
        {
            return new ParseResult(ParseStatus.UnknownPacketId, null, header);
        }

        if (datagram.Length != expectedSize)
        {
            return new ParseResult(ParseStatus.SizeMismatch, null, header, expectedSize);
        }

        Packet packet = header.PacketId switch
        {
            PacketId.Motion => MotionPacket.Read(ref reader, header, layout),
            PacketId.Session => SessionPacket.Read(ref reader, header, layout),
            PacketId.LapData => LapDataPacket.Read(ref reader, header, layout),
            PacketId.Event => EventPacket.Read(datagram, header),
            PacketId.Participants => ParticipantsPacket.Read(ref reader, datagram, header, layout),
            PacketId.CarTelemetry => CarTelemetryPacket.Read(ref reader, header, layout),
            PacketId.CarStatus => CarStatusPacket.Read(ref reader, header, layout),
            PacketId.CarDamage => CarDamagePacket.Read(ref reader, header, layout),
            PacketId.SessionHistory => SessionHistoryPacket.Read(ref reader, header),
            PacketId.CarTelemetry2 => CarTelemetry2Packet.Read(ref reader, header, layout),
            _ => new UnhandledPacket(header),
        };

        return new ParseResult(ParseStatus.Ok, packet, header, expectedSize);
    }
}
