using System.Collections.Frozen;

namespace F1Telemetry.Protocol;

/// <summary>
/// Everything that differs between supported UDP formats lives here, so packet parsers stay
/// format-agnostic and adding a new yearly format is a matter of adding one more layout.
/// </summary>
public sealed class FormatLayout
{
    public const int HeaderSize = 29;

    private FormatLayout(
        GameFormat format,
        int maxCars,
        int motionSlot,
        int telemetrySlot,
        int statusSlot,
        IDictionary<PacketId, int> packetSizes)
    {
        Format = format;
        MaxCars = maxCars;
        MotionSlot = motionSlot;
        TelemetrySlot = telemetrySlot;
        StatusSlot = statusSlot;
        PacketSizes = packetSizes.ToFrozenDictionary();
    }

    public GameFormat Format { get; }

    /// <summary>Number of car slots in every per-car packet (22 in 2025, 24 in 2026).</summary>
    public int MaxCars { get; }

    public int MotionSlot { get; }
    public int LapSlot => 57;
    public int TelemetrySlot { get; }
    public int StatusSlot { get; }
    public int DamageSlot => 46;
    public int Telemetry2Slot => 10;
    public int FinalClassificationSlot => 46;

    /// <summary>2026 widens driverId, networkId and teamId in the participants packet from u8 to u16.</summary>
    public bool WideParticipantIds => Format == GameFormat.F1_26;

    public int ParticipantSlot => WideParticipantIds ? 60 : 57;

    /// <summary>2026 quantises g-forces as int16 / 1000 instead of float.</summary>
    public bool QuantisedGForces => Format == GameFormat.F1_26;

    /// <summary>2026 shrinks engine temperature from u16 to u8.</summary>
    public bool ByteEngineTemperature => Format == GameFormat.F1_26;

    /// <summary>2026 inserts ersHarvestLimitPerLap before ersDeployedThisLap.</summary>
    public bool HasErsHarvestLimit => Format == GameFormat.F1_26;

    /// <summary>2026 appends active aero / DRS zones and assist flags to the session packet.</summary>
    public bool HasSessionAeroBlock => Format == GameFormat.F1_26;

    /// <summary>Exact on-wire size of every packet type. Packets with any other length are rejected.</summary>
    public FrozenDictionary<PacketId, int> PacketSizes { get; }

    public static FormatLayout F1_25 { get; } = new(
        GameFormat.F1_25,
        maxCars: 22,
        motionSlot: 60,
        telemetrySlot: 60,
        statusSlot: 55,
        new Dictionary<PacketId, int>
        {
            [PacketId.Motion] = 1349,
            [PacketId.Session] = 753,
            [PacketId.LapData] = 1285,
            [PacketId.Event] = 45,
            [PacketId.Participants] = 1284,
            [PacketId.CarSetups] = 1133,
            [PacketId.CarTelemetry] = 1352,
            [PacketId.CarStatus] = 1239,
            [PacketId.FinalClassification] = 1042,
            [PacketId.LobbyInfo] = 954,
            [PacketId.CarDamage] = 1041,
            [PacketId.SessionHistory] = 1460,
            [PacketId.TyreSets] = 231,
            [PacketId.MotionEx] = 273,
            [PacketId.TimeTrial] = 101,
            [PacketId.LapPositions] = 1131,
        });

    public static FormatLayout F1_26 { get; } = new(
        GameFormat.F1_26,
        maxCars: 24,
        motionSlot: 54,
        telemetrySlot: 59,
        statusSlot: 59,
        new Dictionary<PacketId, int>
        {
            [PacketId.Motion] = 1325,
            [PacketId.Session] = 926,
            [PacketId.LapData] = 1399,
            [PacketId.Event] = 45,
            [PacketId.Participants] = 1470,
            [PacketId.CarSetups] = 1233,
            [PacketId.CarTelemetry] = 1448,
            [PacketId.CarStatus] = 1445,
            [PacketId.FinalClassification] = 1134,
            [PacketId.LobbyInfo] = 1062,
            [PacketId.CarDamage] = 1133,
            [PacketId.SessionHistory] = 1460,
            [PacketId.TyreSets] = 231,
            [PacketId.MotionEx] = 273,
            [PacketId.TimeTrial] = 104,
            [PacketId.LapPositions] = 1231,
            [PacketId.CarTelemetry2] = 269,
        });

    public static IReadOnlyList<FormatLayout> All { get; } = [F1_25, F1_26];

    public static bool TryGet(ushort packetFormat, out FormatLayout layout)
    {
        switch ((GameFormat)packetFormat)
        {
            case GameFormat.F1_25:
                layout = F1_25;
                return true;
            case GameFormat.F1_26:
                layout = F1_26;
                return true;
            default:
                layout = null!;
                return false;
        }
    }

    public static FormatLayout For(GameFormat format) =>
        TryGet((ushort)format, out var layout) ? layout : throw new NotSupportedException($"Unsupported format {format}");
}
