namespace F1Telemetry.Protocol.Packets;

public readonly record struct LapData(
    uint LastLapTimeMs,
    uint CurrentLapTimeMs,
    uint Sector1TimeMs,
    uint Sector2TimeMs,
    uint DeltaToCarInFrontMs,
    uint DeltaToRaceLeaderMs,
    float LapDistance,
    float TotalDistance,
    float SafetyCarDelta,
    byte CarPosition,
    byte CurrentLapNum,
    PitStatus PitStatus,
    byte NumPitStops,
    byte Sector,
    bool CurrentLapInvalid,
    byte Penalties,
    byte TotalWarnings,
    byte CornerCuttingWarnings,
    byte GridPosition,
    DriverStatus DriverStatus,
    ResultStatus ResultStatus,
    bool PitLaneTimerActive,
    ushort PitLaneTimeInLaneMs,
    ushort PitStopTimerMs,
    float SpeedTrapFastestSpeed)
{
    internal static LapData Read(ref SpanReader r)
    {
        var lastLap = r.U32();
        var currentLap = r.U32();
        var s1 = r.SplitTimeMs();
        var s2 = r.SplitTimeMs();
        var deltaFront = r.SplitTimeMs();
        var deltaLeader = r.SplitTimeMs();
        var lapDistance = r.F32();
        var totalDistance = r.F32();
        var scDelta = r.F32();
        var position = r.U8();
        var lapNum = r.U8();
        var pitStatus = (PitStatus)r.U8();
        var numPitStops = r.U8();
        var sector = r.U8();
        var invalid = r.Bool();
        var penalties = r.U8();
        var warnings = r.U8();
        var cornerCutting = r.U8();
        r.Skip(2); // unserved drive-through / stop-go
        var grid = r.U8();
        var driverStatus = (DriverStatus)r.U8();
        var resultStatus = (ResultStatus)r.U8();
        var pitTimerActive = r.Bool();
        var pitLaneTime = r.U16();
        var pitStopTimer = r.U16();
        r.Skip(1); // pitStopShouldServePen
        var speedTrap = r.F32();
        r.Skip(1); // speedTrapFastestLap
        return new LapData(lastLap, currentLap, s1, s2, deltaFront, deltaLeader, lapDistance, totalDistance, scDelta,
            position, lapNum, pitStatus, numPitStops, sector, invalid, penalties, warnings, cornerCutting, grid,
            driverStatus, resultStatus, pitTimerActive, pitLaneTime, pitStopTimer, speedTrap);
    }
}

public sealed record LapDataPacket(PacketHeader Header, LapData[] Cars, byte TimeTrialPbCarIdx, byte TimeTrialRivalCarIdx)
    : Packet(Header)
{
    internal static LapDataPacket Read(ref SpanReader r, PacketHeader header, FormatLayout layout)
    {
        var cars = new LapData[layout.MaxCars];
        for (var i = 0; i < cars.Length; i++)
        {
            var start = r.Position;
            cars[i] = LapData.Read(ref r);
            r.Position = start + layout.LapSlot;
        }

        return new LapDataPacket(header, cars, r.U8(), r.U8());
    }

    public LapData Player => Cars[Header.PlayerCarIndex];
}

public enum PitStatus : byte
{
    None = 0,
    Pitting = 1,
    InPitArea = 2,
}

public enum DriverStatus : byte
{
    InGarage = 0,
    FlyingLap = 1,
    InLap = 2,
    OutLap = 3,
    OnTrack = 4,
}

public enum ResultStatus : byte
{
    Invalid = 0,
    Inactive = 1,
    Active = 2,
    Finished = 3,
    DidNotFinish = 4,
    Disqualified = 5,
    NotClassified = 6,
    Retired = 7,
}
