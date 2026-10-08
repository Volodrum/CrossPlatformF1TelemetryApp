using System.Globalization;
using F1Telemetry.Protocol;
using F1Telemetry.Protocol.Packets;

namespace F1Telemetry.Core.Setups;

/// <summary>
/// One setting of a car setup: where the game's setup menu shows it, how it reads, and how to get it from and put it
/// into a <see cref="CarSetup"/>. <see cref="Key"/> names it in exported files.
/// </summary>
public sealed record SetupSetting(string Key, string Group, string Label, string Unit, int Decimals, Func<CarSetup, double> Get)
{
    public string Format(CarSetup setup) => Format(Get(setup));

    public string Format(double value) => value.ToString(Decimals == 0 ? "0" : "0." + new string('0', Decimals), CultureInfo.InvariantCulture);

    /// <summary>Whether two values differ in what the menu shows (floats sent by the game carry noise below that).</summary>
    public bool Differs(CarSetup a, CarSetup b) => Math.Abs(Get(a) - Get(b)) >= Math.Pow(10, -Decimals) / 2;
}

/// <summary>Every setting of a <see cref="CarSetup"/>, grouped and ordered like the in-game setup menu.</summary>
public static class SetupSettings
{
    public const string Aerodynamics = "Aerodynamics";
    public const string Transmission = "Transmission";
    public const string Geometry = "Suspension geometry";
    public const string Suspension = "Suspension";
    public const string Brakes = "Brakes";
    public const string Tyres = "Tyres";
    public const string Other = "Other";

    public static IReadOnlyList<SetupSetting> All { get; } =
    [
        new("frontWing", Aerodynamics, "Front wing", "", 0, s => s.FrontWing),
        new("rearWing", Aerodynamics, "Rear wing", "", 0, s => s.RearWing),
        new("onThrottle", Transmission, "Differential on throttle", "%", 0, s => s.OnThrottle),
        new("offThrottle", Transmission, "Differential off throttle", "%", 0, s => s.OffThrottle),
        new("engineBraking", Transmission, "Engine braking", "%", 0, s => s.EngineBraking),
        new("frontCamber", Geometry, "Front camber", "°", 2, s => s.FrontCamber),
        new("rearCamber", Geometry, "Rear camber", "°", 2, s => s.RearCamber),
        new("frontToe", Geometry, "Front toe-out", "°", 2, s => s.FrontToe),
        new("rearToe", Geometry, "Rear toe-in", "°", 2, s => s.RearToe),
        new("frontSuspension", Suspension, "Front suspension", "", 0, s => s.FrontSuspension),
        new("rearSuspension", Suspension, "Rear suspension", "", 0, s => s.RearSuspension),
        new("frontAntiRollBar", Suspension, "Front anti-roll bar", "", 0, s => s.FrontAntiRollBar),
        new("rearAntiRollBar", Suspension, "Rear anti-roll bar", "", 0, s => s.RearAntiRollBar),
        new("frontRideHeight", Suspension, "Front ride height", "", 0, s => s.FrontSuspensionHeight),
        new("rearRideHeight", Suspension, "Rear ride height", "", 0, s => s.RearSuspensionHeight),
        new("brakePressure", Brakes, "Brake pressure", "%", 0, s => s.BrakePressure),
        new("brakeBias", Brakes, "Front brake bias", "%", 0, s => s.BrakeBias),
        new("tyrePressureFrontLeft", Tyres, "Front left pressure", "psi", 1, s => s.TyresPressure.FrontLeft),
        new("tyrePressureFrontRight", Tyres, "Front right pressure", "psi", 1, s => s.TyresPressure.FrontRight),
        new("tyrePressureRearLeft", Tyres, "Rear left pressure", "psi", 1, s => s.TyresPressure.RearLeft),
        new("tyrePressureRearRight", Tyres, "Rear right pressure", "psi", 1, s => s.TyresPressure.RearRight),
        new("ballast", Other, "Ballast", "", 0, s => s.Ballast),
    ];

    /// <summary>The settings that differ between <paramref name="a"/> and <paramref name="b"/>.</summary>
    public static IReadOnlyList<SetupSetting> Differences(CarSetup a, CarSetup b) => [.. All.Where(s => s.Differs(a, b))];

    /// <summary>Settings by <see cref="SetupSetting.Key"/>.</summary>
    public static IReadOnlyDictionary<string, double> ToValues(CarSetup setup) => All.ToDictionary(s => s.Key, s => s.Get(setup));

    /// <summary>A setup from <see cref="ToValues"/>' keys. Throws when one is missing or out of range.</summary>
    public static CarSetup FromValues(IReadOnlyDictionary<string, double> values)
    {
        double V(string key) => values.TryGetValue(key, out var v) && double.IsFinite(v) ? v : throw new FormatException($"Setting \"{key}\" is missing.");
        byte B(string key) => V(key) is >= 0 and <= 255 and var v ? (byte)Math.Round(v) : throw new FormatException($"Setting \"{key}\" is out of range.");
        float F(string key) => (float)V(key);

        return new CarSetup(
            FrontWing: B("frontWing"),
            RearWing: B("rearWing"),
            OnThrottle: B("onThrottle"),
            OffThrottle: B("offThrottle"),
            FrontCamber: F("frontCamber"),
            RearCamber: F("rearCamber"),
            FrontToe: F("frontToe"),
            RearToe: F("rearToe"),
            FrontSuspension: B("frontSuspension"),
            RearSuspension: B("rearSuspension"),
            FrontAntiRollBar: B("frontAntiRollBar"),
            RearAntiRollBar: B("rearAntiRollBar"),
            FrontSuspensionHeight: B("frontRideHeight"),
            RearSuspensionHeight: B("rearRideHeight"),
            BrakePressure: B("brakePressure"),
            BrakeBias: B("brakeBias"),
            EngineBraking: B("engineBraking"),
            TyresPressure: new Tyres<float>(F("tyrePressureRearLeft"), F("tyrePressureRearRight"), F("tyrePressureFrontLeft"), F("tyrePressureFrontRight")),
            Ballast: B("ballast"),
            FuelLoad: 0);
    }
}
