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

    public static bool IsRace(int sessionType) => sessionType is >= 15 and <= 17;

    public static bool IsQualifying(int sessionType) => sessionType is >= 5 and <= 14;
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
