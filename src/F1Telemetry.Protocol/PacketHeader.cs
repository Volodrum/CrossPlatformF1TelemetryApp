namespace F1Telemetry.Protocol;

/// <summary>29-byte header shared by every packet in both supported formats.</summary>
public readonly record struct PacketHeader(
    ushort PacketFormat,
    byte GameYear,
    byte GameMajorVersion,
    byte GameMinorVersion,
    byte PacketVersion,
    PacketId PacketId,
    ulong SessionUid,
    float SessionTime,
    uint FrameIdentifier,
    uint OverallFrameIdentifier,
    byte PlayerCarIndex,
    byte SecondaryPlayerCarIndex)
{
    public const int PacketIdOffset = 6;

    public GameFormat Format => (GameFormat)PacketFormat;

    public static PacketHeader Read(ref SpanReader r) => new(
        PacketFormat: r.U16(),
        GameYear: r.U8(),
        GameMajorVersion: r.U8(),
        GameMinorVersion: r.U8(),
        PacketVersion: r.U8(),
        PacketId: (PacketId)r.U8(),
        SessionUid: r.U64(),
        SessionTime: r.F32(),
        FrameIdentifier: r.U32(),
        OverallFrameIdentifier: r.U32(),
        PlayerCarIndex: r.U8(),
        SecondaryPlayerCarIndex: r.U8());
}
