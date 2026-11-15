using F1Telemetry.Protocol;

namespace F1Telemetry.Core.Games;

/// <summary>A game the app can show telemetry for (or is planned to).</summary>
public enum GameId
{
    /// <summary>EA SPORTS F1 25, with its 2026 Season Pack ("F1 26").</summary>
    F1,

    /// <summary>Le Mans Ultimate.</summary>
    LeMansUltimate,

    /// <summary>Assetto Corsa Competizione.</summary>
    AssettoCorsaCompetizione,
}

/// <summary>
/// What a game's telemetry carries, beyond the laps, inputs and positions every game has. The parts of the app built on
/// a feature are shown only for games that have it, so a game is added without each page asking which game it is.
/// </summary>
[Flags]
public enum GameFeatures
{
    None = 0,

    /// <summary>Several dry compounds to choose between: the compound library and race plans.</summary>
    TyreCompounds = 1 << 0,

    /// <summary>A battery the driver deploys by mode: the energy tab and the ERS plan overlay.</summary>
    Ers = 1 << 1,

    /// <summary>The car's setup arrives with the telemetry: the setup library.</summary>
    Setups = 1 << 2,

    /// <summary>The game sends its sector times and the field's state: the sector box and timing tower.</summary>
    TimingFeed = 1 << 3,

    /// <summary>Car damage per part: the damage page of the conditions overlay.</summary>
    Damage = 1 << 4,

    /// <summary>The weather forecast for the session.</summary>
    Forecast = 1 << 5,
}

/// <summary>A telemetry mode of a game the user picks in the toolbar (it must match what the game sends).</summary>
public sealed record GameMode(string Label, GameFormat Format);

/// <param name="ShortName">For the navigation rail and tight places ("F1", "LMU").</param>
/// <param name="Supported">False for a game that is planned: listed, but it can't be selected yet.</param>
/// <param name="Modes">Telemetry modes to switch between; one or none means there is nothing to pick.</param>
/// <param name="Connection">How the app gets the game's telemetry, in a few words.</param>
public sealed record GameInfo(
    GameId Id,
    string Name,
    string ShortName,
    string Detail,
    bool Supported,
    GameFeatures Features,
    IReadOnlyList<GameMode> Modes,
    string Connection)
{
    public bool Has(GameFeatures feature) => (Features & feature) == feature;
}

/// <summary>
/// The games the app knows. Everything that differs between games hangs off a <see cref="GameInfo"/>: its name, its
/// telemetry modes, and the features that decide which pages and overlays apply. Adding a game means an entry here, a
/// packet source and an engine for its telemetry, and nothing in the pages themselves.
/// </summary>
public static class GameCatalog
{
    public static GameInfo F1 { get; } = new(
        GameId.F1,
        "F1 25",
        "F1",
        "EA SPORTS F1 25 with the 2026 Season Pack",
        Supported: true,
        GameFeatures.TyreCompounds | GameFeatures.Ers | GameFeatures.Setups | GameFeatures.TimingFeed | GameFeatures.Damage | GameFeatures.Forecast,
        [new GameMode("F1 25", GameFormat.F1_25), new GameMode("F1 26", GameFormat.F1_26)],
        "UDP telemetry");

    public static GameInfo LeMansUltimate { get; } = new(
        GameId.LeMansUltimate,
        "Le Mans Ultimate",
        "LMU",
        "Hypercar, LMP2 and GT3 endurance racing",
        Supported: false,
        GameFeatures.TyreCompounds | GameFeatures.Ers | GameFeatures.Damage,
        [],
        "shared memory");

    public static GameInfo AssettoCorsaCompetizione { get; } = new(
        GameId.AssettoCorsaCompetizione,
        "Assetto Corsa Competizione",
        "ACC",
        "GT3 and GT4 racing",
        Supported: false,
        GameFeatures.Damage,
        [],
        "shared memory");

    public static IReadOnlyList<GameInfo> All { get; } = [F1, LeMansUltimate, AssettoCorsaCompetizione];

    /// <summary>The game with this id; an unknown id (settings from a newer version) gives the default game.</summary>
    public static GameInfo Get(GameId id) => All.FirstOrDefault(g => g.Id == id) ?? F1;

    /// <summary>The game a recording in this telemetry format came from.</summary>
    public static GameInfo ForFormat(GameFormat format) => All.FirstOrDefault(g => g.Modes.Any(m => m.Format == format)) ?? F1;

    /// <summary>What a telemetry format is called on screen ("F1 25", "F1 26").</summary>
    public static string FormatName(GameFormat format) =>
        ForFormat(format).Modes.FirstOrDefault(m => m.Format == format)?.Label ?? format.ToString();
}
