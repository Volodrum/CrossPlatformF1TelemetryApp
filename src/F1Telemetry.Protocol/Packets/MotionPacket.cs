namespace F1Telemetry.Protocol.Packets;

public readonly record struct CarMotion(
    float WorldPositionX,
    float WorldPositionY,
    float WorldPositionZ,
    float WorldVelocityX,
    float WorldVelocityY,
    float WorldVelocityZ,
    float ForwardX,
    float ForwardY,
    float ForwardZ,
    float RightX,
    float RightY,
    float RightZ,
    float GForceLateral,
    float GForceLongitudinal,
    float GForceVertical,
    float Yaw,
    float Pitch,
    float Roll)
{
    internal static CarMotion Read(ref SpanReader r, FormatLayout layout)
    {
        float px = r.F32(), py = r.F32(), pz = r.F32();
        float vx = r.F32(), vy = r.F32(), vz = r.F32();
        float fx = r.Normalised16(), fy = r.Normalised16(), fz = r.Normalised16();
        float rx = r.Normalised16(), ry = r.Normalised16(), rz = r.Normalised16();
        float gLat, gLon, gVert;
        if (layout.QuantisedGForces)
        {
            gLat = r.I16() / 1000f;
            gLon = r.I16() / 1000f;
            gVert = r.I16() / 1000f;
        }
        else
        {
            gLat = r.F32();
            gLon = r.F32();
            gVert = r.F32();
        }

        return new CarMotion(px, py, pz, vx, vy, vz, fx, fy, fz, rx, ry, rz, gLat, gLon, gVert, r.F32(), r.F32(), r.F32());
    }
}

public sealed record MotionPacket(PacketHeader Header, CarMotion[] Cars) : Packet(Header)
{
    internal static MotionPacket Read(ref SpanReader r, PacketHeader header, FormatLayout layout)
    {
        var cars = new CarMotion[layout.MaxCars];
        for (var i = 0; i < cars.Length; i++)
        {
            cars[i] = CarMotion.Read(ref r, layout);
        }

        return new MotionPacket(header, cars);
    }

    public CarMotion Player => Cars[Header.PlayerCarIndex];
}
