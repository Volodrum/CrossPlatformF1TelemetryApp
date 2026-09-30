namespace F1Telemetry.Protocol.Packets;

public readonly record struct CarStatus(
    byte TractionControl,
    byte AntiLockBrakes,
    byte FuelMix,
    byte FrontBrakeBias,
    bool PitLimiter,
    float FuelInTank,
    float FuelCapacity,
    float FuelRemainingLaps,
    ushort MaxRpm,
    ushort IdleRpm,
    byte MaxGears,
    bool DrsAllowed,
    ushort DrsActivationDistance,
    byte ActualTyreCompound,
    byte VisualTyreCompound,
    byte TyresAgeLaps,
    sbyte VehicleFiaFlags,
    float EnginePowerIce,
    float EnginePowerMguk,
    float ErsStoreEnergy,
    byte ErsDeployMode,
    float ErsHarvestedThisLapMguk,
    float ErsHarvestedThisLapMguh,
    float? ErsHarvestLimitPerLap,
    float ErsDeployedThisLap,
    bool NetworkPaused)
{
    internal static CarStatus Read(ref SpanReader r, FormatLayout layout) => new(
        TractionControl: r.U8(),
        AntiLockBrakes: r.U8(),
        FuelMix: r.U8(),
        FrontBrakeBias: r.U8(),
        PitLimiter: r.Bool(),
        FuelInTank: r.F32(),
        FuelCapacity: r.F32(),
        FuelRemainingLaps: r.F32(),
        MaxRpm: r.U16(),
        IdleRpm: r.U16(),
        MaxGears: r.U8(),
        DrsAllowed: r.Bool(),
        DrsActivationDistance: r.U16(),
        ActualTyreCompound: r.U8(),
        VisualTyreCompound: r.U8(),
        TyresAgeLaps: r.U8(),
        VehicleFiaFlags: r.I8(),
        EnginePowerIce: r.F32(),
        EnginePowerMguk: r.F32(),
        ErsStoreEnergy: r.F32(),
        ErsDeployMode: r.U8(),
        ErsHarvestedThisLapMguk: r.F32(),
        ErsHarvestedThisLapMguh: r.F32(),
        ErsHarvestLimitPerLap: layout.HasErsHarvestLimit ? r.F32() : null,
        ErsDeployedThisLap: r.F32(),
        NetworkPaused: r.Bool());
}

public sealed record CarStatusPacket(PacketHeader Header, CarStatus[] Cars) : Packet(Header)
{
    internal static CarStatusPacket Read(ref SpanReader r, PacketHeader header, FormatLayout layout)
    {
        var cars = new CarStatus[layout.MaxCars];
        for (var i = 0; i < cars.Length; i++)
        {
            cars[i] = CarStatus.Read(ref r, layout);
        }

        return new CarStatusPacket(header, cars);
    }

    public CarStatus Player => Cars[Header.PlayerCarIndex];
}
