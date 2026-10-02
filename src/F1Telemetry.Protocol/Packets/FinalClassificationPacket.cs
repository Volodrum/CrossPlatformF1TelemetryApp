namespace F1Telemetry.Protocol.Packets;

/// <param name="TotalRaceTime">Seconds from the start to the flag, without penalties.</param>
public readonly record struct FinalClassification(
    byte Position,
    byte NumLaps,
    byte GridPosition,
    byte Points,
    byte NumPitStops,
    ResultStatus ResultStatus,
    uint BestLapTimeMs,
    double TotalRaceTime,
    byte PenaltiesTime)
{
    internal static FinalClassification Read(ref SpanReader r)
    {
        var position = r.U8();
        var numLaps = r.U8();
        var grid = r.U8();
        var points = r.U8();
        var pitStops = r.U8();
        var resultStatus = (ResultStatus)r.U8();
        r.Skip(1); // resultReason
        var bestLap = r.U32();
        var totalRaceTime = r.F64();
        var penaltiesTime = r.U8();
        return new FinalClassification(position, numLaps, grid, points, pitStops, resultStatus, bestLap, totalRaceTime, penaltiesTime);
    }
}

/// <summary>Sent once, when the session ends (the race result screen).</summary>
public sealed record FinalClassificationPacket(PacketHeader Header, FinalClassification[] Cars) : Packet(Header)
{
    internal static FinalClassificationPacket Read(ref SpanReader r, PacketHeader header, FormatLayout layout)
    {
        r.Skip(1); // numCars
        var cars = new FinalClassification[layout.MaxCars];
        for (var i = 0; i < cars.Length; i++)
        {
            var start = r.Position;
            cars[i] = FinalClassification.Read(ref r);
            r.Position = start + layout.FinalClassificationSlot;
        }

        return new FinalClassificationPacket(header, cars);
    }

    public FinalClassification Player => Cars[Header.PlayerCarIndex];
}
