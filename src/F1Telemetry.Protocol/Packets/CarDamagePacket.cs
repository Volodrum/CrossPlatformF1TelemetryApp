namespace F1Telemetry.Protocol.Packets;

public readonly record struct CarDamage(
    Tyres<float> TyresWear,
    Tyres<byte> TyresDamage,
    Tyres<byte> BrakesDamage,
    Tyres<byte> TyreBlisters,
    byte FrontLeftWingDamage,
    byte FrontRightWingDamage,
    byte RearWingDamage,
    byte FloorDamage,
    byte DiffuserDamage,
    byte SidepodDamage,
    bool DrsFault,
    bool ErsFault,
    byte GearBoxDamage,
    byte EngineDamage,
    byte EngineMguhWear,
    byte EngineEsWear,
    byte EngineCeWear,
    byte EngineIceWear,
    byte EngineMgukWear,
    byte EngineTcWear,
    bool EngineBlown,
    bool EngineSeized)
{
    public float AverageTyreWear => (TyresWear.RearLeft + TyresWear.RearRight + TyresWear.FrontLeft + TyresWear.FrontRight) / 4f;

    internal static CarDamage Read(ref SpanReader r) => new(
        r.TyresF32(), r.TyresU8(), r.TyresU8(), r.TyresU8(),
        r.U8(), r.U8(), r.U8(), r.U8(), r.U8(), r.U8(),
        r.Bool(), r.Bool(),
        r.U8(), r.U8(), r.U8(), r.U8(), r.U8(), r.U8(), r.U8(), r.U8(),
        r.Bool(), r.Bool());
}

public sealed record CarDamagePacket(PacketHeader Header, CarDamage[] Cars) : Packet(Header)
{
    internal static CarDamagePacket Read(ref SpanReader r, PacketHeader header, FormatLayout layout)
    {
        var cars = new CarDamage[layout.MaxCars];
        for (var i = 0; i < cars.Length; i++)
        {
            cars[i] = CarDamage.Read(ref r);
        }

        return new CarDamagePacket(header, cars);
    }

    public CarDamage Player => Cars[Header.PlayerCarIndex];
}
