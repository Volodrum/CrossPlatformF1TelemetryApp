namespace F1Telemetry.Protocol.Packets;

/// <summary>Base type for every decoded packet. Consumers switch on the concrete type.</summary>
public abstract record Packet(PacketHeader Header)
{
    public FormatLayout Layout => FormatLayout.For(Header.Format);
}

/// <summary>A packet whose id and size are valid but that this application does not decode.</summary>
public sealed record UnhandledPacket(PacketHeader Header) : Packet(Header);
