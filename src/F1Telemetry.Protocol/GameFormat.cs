namespace F1Telemetry.Protocol;

/// <summary>
/// UDP format selected in the game's telemetry settings. The value is carried in <c>m_packetFormat</c>
/// of every packet header, so the format is detected per packet and never needs configuring.
/// </summary>
public enum GameFormat : ushort
{
    /// <summary>Base F1 25 output.</summary>
    F1_25 = 2025,

    /// <summary>F1 25 with the 2026 Season Pack ("F1 26" DLC) UDP mode.</summary>
    F1_26 = 2026,
}

public enum PacketId : byte
{
    Motion = 0,
    Session = 1,
    LapData = 2,
    Event = 3,
    Participants = 4,
    CarSetups = 5,
    CarTelemetry = 6,
    CarStatus = 7,
    FinalClassification = 8,
    LobbyInfo = 9,
    CarDamage = 10,
    SessionHistory = 11,
    TyreSets = 12,
    MotionEx = 13,
    TimeTrial = 14,
    LapPositions = 15,

    /// <summary>2026 Season Pack only.</summary>
    CarTelemetry2 = 16,
}
