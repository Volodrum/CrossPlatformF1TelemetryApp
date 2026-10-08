namespace F1Telemetry.Protocol.Packets;

/// <summary>
/// One car's setup, fields in wire order. Identical in the 2025 and 2026 formats
/// (<see cref="FormatLayout.SetupSlot"/> bytes).
/// In multiplayer only the player's own car is filled in; other cars arrive as all zeros.
/// </summary>
/// <param name="OnThrottle">Differential adjustment on throttle, percent.</param>
/// <param name="OffThrottle">Differential adjustment off throttle, percent.</param>
/// <param name="FrontCamber">Degrees (negative).</param>
/// <param name="FrontToe">Degrees.</param>
/// <param name="FrontSuspensionHeight">Ride height, menu clicks.</param>
/// <param name="BrakePressure">Percent.</param>
/// <param name="BrakeBias">Percent towards the front.</param>
/// <param name="EngineBraking">Percent.</param>
/// <param name="TyresPressure">PSI.</param>
/// <param name="FuelLoad">As sent by the game; not yet confirmed to be kilograms.</param>
public readonly record struct CarSetup(
    byte FrontWing,
    byte RearWing,
    byte OnThrottle,
    byte OffThrottle,
    float FrontCamber,
    float RearCamber,
    float FrontToe,
    float RearToe,
    byte FrontSuspension,
    byte RearSuspension,
    byte FrontAntiRollBar,
    byte RearAntiRollBar,
    byte FrontSuspensionHeight,
    byte RearSuspensionHeight,
    byte BrakePressure,
    byte BrakeBias,
    byte EngineBraking,
    Tyres<float> TyresPressure,
    byte Ballast,
    float FuelLoad)
{
    /// <summary>All zeros: a car whose setup the game does not share, or no setup loaded yet.</summary>
    public bool IsEmpty => this == default;

    /// <summary>
    /// Whether two setups are the same car setup. Fuel load is left out: the game reports the fuel in the tank, which
    /// changes without anyone touching the setup.
    /// </summary>
    public bool SameSettings(CarSetup other) => this with { FuelLoad = 0 } == other with { FuelLoad = 0 };

    internal static CarSetup Read(ref SpanReader r) => new(
        FrontWing: r.U8(),
        RearWing: r.U8(),
        OnThrottle: r.U8(),
        OffThrottle: r.U8(),
        FrontCamber: r.F32(),
        RearCamber: r.F32(),
        FrontToe: r.F32(),
        RearToe: r.F32(),
        FrontSuspension: r.U8(),
        RearSuspension: r.U8(),
        FrontAntiRollBar: r.U8(),
        RearAntiRollBar: r.U8(),
        FrontSuspensionHeight: r.U8(),
        RearSuspensionHeight: r.U8(),
        BrakePressure: r.U8(),
        BrakeBias: r.U8(),
        EngineBraking: r.U8(),
        TyresPressure: r.TyresF32(),
        Ballast: r.U8(),
        FuelLoad: r.F32());
}

/// <param name="NextFrontWingValue">Front wing the player's car will get at its next pit stop.</param>
public sealed record CarSetupsPacket(PacketHeader Header, CarSetup[] Cars, float NextFrontWingValue) : Packet(Header)
{
    internal static CarSetupsPacket Read(ref SpanReader r, PacketHeader header, FormatLayout layout)
    {
        var cars = new CarSetup[layout.MaxCars];
        for (var i = 0; i < cars.Length; i++)
        {
            cars[i] = CarSetup.Read(ref r);
        }

        return new CarSetupsPacket(header, cars, r.F32());
    }

    public CarSetup Player => Cars[Header.PlayerCarIndex];
}
