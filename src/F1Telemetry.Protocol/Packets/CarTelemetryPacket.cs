namespace F1Telemetry.Protocol.Packets;

public readonly record struct CarTelemetry(
    ushort Speed,
    float Throttle,
    float Steer,
    float Brake,
    byte Clutch,
    sbyte Gear,
    ushort EngineRpm,
    bool Drs,
    byte RevLightsPercent,
    Tyres<ushort> BrakesTemperature,
    Tyres<byte> TyresSurfaceTemperature,
    Tyres<byte> TyresInnerTemperature,
    ushort EngineTemperature,
    Tyres<float> TyresPressure,
    Tyres<byte> SurfaceType)
{
    internal static CarTelemetry Read(ref SpanReader r, FormatLayout layout)
    {
        var speed = r.U16();
        var throttle = r.F32();
        var steer = r.F32();
        var brake = r.F32();
        var clutch = r.U8();
        var gear = r.I8();
        var rpm = r.U16();
        var drs = r.Bool();
        var revPercent = r.U8();
        r.Skip(2); // revLightsBitValue
        var brakesTemp = r.TyresU16();
        var surfaceTemp = r.TyresU8();
        var innerTemp = r.TyresU8();
        ushort engineTemp = layout.ByteEngineTemperature ? r.U8() : r.U16();
        var pressure = r.TyresF32();
        var surface = r.TyresU8();
        return new CarTelemetry(speed, throttle, steer, brake, clutch, gear, rpm, drs, revPercent, brakesTemp,
            surfaceTemp, innerTemp, engineTemp, pressure, surface);
    }
}

public sealed record CarTelemetryPacket(PacketHeader Header, CarTelemetry[] Cars, byte MfdPanelIndex, sbyte SuggestedGear)
    : Packet(Header)
{
    internal static CarTelemetryPacket Read(ref SpanReader r, PacketHeader header, FormatLayout layout)
    {
        var cars = new CarTelemetry[layout.MaxCars];
        for (var i = 0; i < cars.Length; i++)
        {
            cars[i] = CarTelemetry.Read(ref r, layout);
        }

        var mfd = r.U8();
        r.Skip(1); // secondary player MFD
        return new CarTelemetryPacket(header, cars, mfd, r.I8());
    }

    public CarTelemetry Player => Cars[Header.PlayerCarIndex];
}

/// <summary>2026 Season Pack only: active aero and overtake mode state.</summary>
public readonly record struct CarTelemetry2(
    byte ActiveAeroMode,
    bool ActiveAeroAvailable,
    ushort ActiveAeroActivationDistance,
    bool OvertakeAvailable,
    bool OvertakeActive,
    ushort OvertakeActivationDistance,
    bool Regulations2026Applicable,
    bool IsDrivingWrongWay);

public sealed record CarTelemetry2Packet(PacketHeader Header, CarTelemetry2[] Cars) : Packet(Header)
{
    internal static CarTelemetry2Packet Read(ref SpanReader r, PacketHeader header, FormatLayout layout)
    {
        var cars = new CarTelemetry2[layout.MaxCars];
        for (var i = 0; i < cars.Length; i++)
        {
            cars[i] = new CarTelemetry2(r.U8(), r.Bool(), r.U16(), r.Bool(), r.Bool(), r.U16(), r.Bool(), r.Bool());
        }

        return new CarTelemetry2Packet(header, cars);
    }

    public CarTelemetry2 Player => Cars[Header.PlayerCarIndex];
}
