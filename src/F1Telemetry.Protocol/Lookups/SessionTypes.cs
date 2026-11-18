namespace F1Telemetry.Protocol.Lookups;

public static class SessionTypes
{
    private static readonly string[] Names =
    [
        "Unknown", "Practice 1", "Practice 2", "Practice 3", "Short Practice",
        "Qualifying 1", "Qualifying 2", "Qualifying 3", "Short Qualifying", "One-Shot Qualifying",
        "Sprint Shootout 1", "Sprint Shootout 2", "Sprint Shootout 3", "Short Sprint Shootout", "One-Shot Sprint Shootout",
        "Race", "Race 2", "Race 3", "Time Trial",
    ];

    public const int TimeTrial = 18;

    public static string Name(int sessionType) =>
        sessionType >= 0 && sessionType < Names.Length ? Names[sessionType] : $"Session {sessionType}";

    public static bool IsPractice(int sessionType) => sessionType is >= 1 and <= 4;

    public static bool IsRace(int sessionType) => sessionType is >= 15 and <= 17;

    public static bool IsQualifying(int sessionType) => sessionType is >= 5 and <= 14;
}

/// <summary>
/// The game's session length setting (session packet <c>m_sessionLength</c>). In a race it is the race distance, which
/// the game scales tyre wear and fuel burn to.
/// </summary>
public static class RaceLengths
{
    public const int Unknown = 0;
    public const int VeryShort = 2;
    public const int Short = 3;
    public const int Medium = 4;
    public const int MediumLong = 5;
    public const int Long = 6;
    public const int Full = 7;

    /// <summary>Every length the game offers, shortest first.</summary>
    public static IReadOnlyList<int> All { get; } = [VeryShort, Short, Medium, MediumLong, Long, Full];

    public static bool IsKnown(int length) => length is >= VeryShort and <= Full;

    public static string Name(int length) => length switch
    {
        VeryShort => "Very short",
        Short => "Short",
        Medium => "Medium",
        MediumLong => "Medium long",
        Long => "Long",
        Full => "Full",
        _ => "Any length",
    };
}

public static class SafetyCarStatus
{
    public const byte None = 0;
    public const byte Full = 1;
    public const byte Virtual = 2;
    public const byte FormationLap = 3;
}

public static class Formulas
{
    public static string Name(byte formula) => formula switch
    {
        0 => "F1 Modern",
        1 => "F1 Classic",
        2 => "F2",
        3 => "F1 Generic",
        4 => "Beta",
        6 => "Esports",
        8 => "F1 World",
        9 => "F1 Elimination",
        13 => "F1 26",
        _ => $"Formula {formula}",
    };
}
