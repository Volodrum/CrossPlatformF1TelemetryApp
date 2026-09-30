namespace F1Telemetry.Protocol.Packets;

public readonly record struct LapHistory(uint LapTimeMs, uint Sector1Ms, uint Sector2Ms, uint Sector3Ms, byte ValidFlags)
{
    public bool IsLapValid => (ValidFlags & 0x01) != 0;
    public bool IsSector1Valid => (ValidFlags & 0x02) != 0;
    public bool IsSector2Valid => (ValidFlags & 0x04) != 0;
    public bool IsSector3Valid => (ValidFlags & 0x08) != 0;
}

/// <param name="EndLap">Last lap of the stint (1-based), or 255 for the stint currently running.</param>
public readonly record struct TyreStint(byte EndLap, byte ActualCompound, byte VisualCompound)
{
    public const byte Current = 255;
}

/// <summary>
/// Lap and sector history for one car. The game rotates through all cars, sending one of these per car
/// roughly once a second. <c>Laps[i]</c> is lap number <c>i + 1</c>.
/// </summary>
public sealed record SessionHistoryPacket(
    PacketHeader Header,
    byte CarIndex,
    byte BestLapNum,
    byte BestSector1LapNum,
    byte BestSector2LapNum,
    byte BestSector3LapNum,
    LapHistory[] Laps,
    TyreStint[] Stints) : Packet(Header)
{
    private const int MaxLaps = 100;
    private const int MaxStints = 8;
    private const int LapHistorySize = 14;

    internal static SessionHistoryPacket Read(ref SpanReader r, PacketHeader header)
    {
        var carIdx = r.U8();
        var numLaps = Math.Min((int)r.U8(), MaxLaps);
        var numStints = Math.Min((int)r.U8(), MaxStints);
        var bestLap = r.U8();
        var bestS1 = r.U8();
        var bestS2 = r.U8();
        var bestS3 = r.U8();

        var lapsStart = r.Position;
        var laps = new LapHistory[numLaps];
        for (var i = 0; i < numLaps; i++)
        {
            laps[i] = new LapHistory(r.U32(), r.SplitTimeMs(), r.SplitTimeMs(), r.SplitTimeMs(), r.U8());
        }

        r.Position = lapsStart + MaxLaps * LapHistorySize;
        var stints = new TyreStint[numStints];
        for (var i = 0; i < numStints; i++)
        {
            stints[i] = new TyreStint(r.U8(), r.U8(), r.U8());
        }

        return new SessionHistoryPacket(header, carIdx, bestLap, bestS1, bestS2, bestS3, laps, stints);
    }

    public bool IsPlayer => CarIndex == Header.PlayerCarIndex;

    /// <summary>Index into <see cref="Stints"/> for a 1-based lap number, or -1 if unknown.</summary>
    public int StintIndexForLap(int lapNumber)
    {
        for (var i = 0; i < Stints.Length; i++)
        {
            if (Stints[i].EndLap == TyreStint.Current || lapNumber <= Stints[i].EndLap)
            {
                return i;
            }
        }

        return -1;
    }
}
