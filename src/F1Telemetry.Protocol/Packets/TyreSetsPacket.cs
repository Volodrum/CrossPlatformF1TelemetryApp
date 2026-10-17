namespace F1Telemetry.Protocol.Packets;

/// <summary>
/// One tyre set of a car's weekend allocation, fields in wire order (10 bytes, the same in the 2025 and 2026 formats).
/// </summary>
/// <param name="Wear">Wear of the set, percent (0 = new).</param>
/// <param name="Available">Whether the set can still be used in this session.</param>
/// <param name="RecommendedSession">Session type the game recommends the set for (see <see cref="Lookups.SessionTypes"/>).</param>
/// <param name="LifeSpan">Laps left in the set.</param>
/// <param name="UsableLife">Most laps the game recommends on this compound.</param>
/// <param name="LapDeltaMs">Lap time on this set against the set fitted, ms.</param>
public readonly record struct TyreSet(
    byte ActualCompound,
    byte VisualCompound,
    byte Wear,
    bool Available,
    byte RecommendedSession,
    byte LifeSpan,
    byte UsableLife,
    short LapDeltaMs,
    bool Fitted)
{
    public const int Size = 10;

    /// <summary>All zeros: a slot the game leaves empty.</summary>
    public bool IsEmpty => this == default;

    internal static TyreSet Read(ref SpanReader r) => new(
        ActualCompound: r.U8(),
        VisualCompound: r.U8(),
        Wear: r.U8(),
        Available: r.Bool(),
        RecommendedSession: r.U8(),
        LifeSpan: r.U8(),
        UsableLife: r.U8(),
        LapDeltaMs: r.I16(),
        Fitted: r.Bool());
}

/// <summary>
/// The tyre sets of one car: 13 dry and 7 wet. The game cycles through the cars, so each car's sets arrive about once
/// a second.
/// </summary>
/// <param name="FittedIndex">Index into <see cref="Sets"/> of the set on the car.</param>
public sealed record TyreSetsPacket(PacketHeader Header, byte CarIndex, TyreSet[] Sets, byte FittedIndex) : Packet(Header)
{
    public const int SetCount = 20;
    public const int DrySetCount = 13;

    internal static TyreSetsPacket Read(ref SpanReader r, PacketHeader header)
    {
        var car = r.U8();
        var sets = new TyreSet[SetCount];
        for (var i = 0; i < sets.Length; i++)
        {
            sets[i] = TyreSet.Read(ref r);
        }

        return new TyreSetsPacket(header, car, sets, r.U8());
    }

    public bool IsPlayer => CarIndex == Header.PlayerCarIndex;
}
