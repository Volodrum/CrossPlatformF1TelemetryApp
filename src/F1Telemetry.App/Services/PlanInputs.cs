using F1Telemetry.Core.Energy;
using F1Telemetry.Core.Formatting;
using F1Telemetry.Core.Models;
using F1Telemetry.Protocol.Lookups;

namespace F1Telemetry.App.Services;

/// <summary>A session the plans are built on: its typical laps and its fastest lap.</summary>
public sealed record PlanSession(RecordingInfo Recording, IReadOnlyList<LapRecord> Representative, LapRecord? Fastest);

/// <summary>What the planner is given: the car model and the lap each kind of plan is built on.</summary>
/// <param name="Race">The typical lap (the sessions' typical laps averaged): a race plan must hold for every one of them.</param>
/// <param name="RaceLaps">How many typical laps <paramref name="Race"/> averages.</param>
/// <param name="Qualifying">The fastest lap of the sessions: one push lap.</param>
public sealed record PlanInputs(ErsCarModel Model, LapProfile Race, int RaceLaps, string RaceBasis, LapProfile Qualifying, string QualifyingBasis)
{
    private const string Reimport = "Recordings made before the app stored engine power can get it from their raw capture: close the app and run "
        + "f1tel reimport --db <telemetry.duckdb> <capture.f1rec> --replace (see the README). New recordings have it.";

    /// <summary>
    /// The laps a session's plans are built on, as the energy tab shows them: race plans on its typical laps averaged,
    /// qualifying plans on its fastest lap. Null, with the reason, when the session can't be planned.
    /// <paramref name="fastestSamples"/> saves loading the fastest lap again when the caller has it.
    /// </summary>
    public static Task<(PlanInputs? Inputs, string Unavailable)> LoadAsync(TelemetryRuntime runtime, RecordingInfo recording, ErsCarModel? model,
        IReadOnlyList<LapRecord> representative, LapRecord? fastest, IReadOnlyList<TelemetrySample>? fastestSamples = null) =>
        LoadAsync(runtime, [new PlanSession(recording, representative, fastest)], model, fastestSamples);

    /// <summary>
    /// The same over several sessions at one track and game format: race plans on all their typical laps averaged,
    /// qualifying plans on the fastest lap among them. <paramref name="fastestSamples"/> are the samples of the first
    /// session's fastest lap, used when that lap is the fastest of all.
    /// </summary>
    public static async Task<(PlanInputs? Inputs, string Unavailable)> LoadAsync(TelemetryRuntime runtime, IReadOnlyList<PlanSession> sessions, ErsCarModel? model,
        IReadOnlyList<TelemetrySample>? fastestSamples = null)
    {
        if (model is not { HasPowerData: true })
        {
            return (null, "No plan: no lap at this track has ICE and MGU-K power data, so there is no car model to plan with. " + Reimport);
        }

        if (model.StraightLine.Count == 0 || model.Deploy.Curves.Count == 0)
        {
            return (null, "No plan yet: the car model needs a straight-line fit and a deploy map first. Record a few more laps at this track.");
        }

        var fastest = sessions.Where(s => s.Fastest is not null).MinBy(s => s.Fastest!.LapTimeMs);
        if (fastest is null)
        {
            return (null, sessions.Count == 1 ? "No plan: this session has no timed lap to build one on." : "No plan: these sessions have no timed lap to build one on.");
        }

        var format = sessions[0].Recording.Format;
        var fastestLap = fastest.Fastest!;
        if (fastest != sessions[0] || fastestSamples is null)
        {
            fastestSamples = await runtime.Store.GetLapSamplesAsync(fastest.Recording.Id, fastestLap.LapNumber);
        }

        var profiles = new List<(LapRecord Lap, LapProfile Profile)>();
        foreach (var session in sessions)
        {
            foreach (var lap in session.Representative)
            {
                var samples = session == fastest && lap.LapNumber == fastestLap.LapNumber
                    ? fastestSamples
                    : await runtime.Store.GetLapSamplesAsync(session.Recording.Id, lap.LapNumber);
                if (await Task.Run(() => LapProfile.From(samples, format, lap.LapTimeMs / 1000.0)) is { } profile)
                {
                    profiles.Add((lap, profile));
                }
            }
        }

        var qualifying = LapProfile.From(fastestSamples, format, fastestLap.LapTimeMs / 1000.0);
        var race = await Task.Run(() => LapProfile.Average([.. profiles.Select(p => p.Profile)]));
        if (race is null || qualifying is null)
        {
            return (null, (sessions.Count == 1 ? "No plan: this session has" : "No plan: these sessions have") + " no ICE and MGU-K power data. " + Reimport);
        }

        var average = TimeFormat.Lap(profiles.Average(p => (double)p.Lap.LapTimeMs));
        string raceBasis, qualifyingBasis;
        if (sessions.Count == 1)
        {
            raceBasis = profiles.Count == 1
                ? $"Built on the session's only typical lap, L{profiles[0].Lap.LapNumber} · {average}."
                : $"Built on the session's typical lap: {profiles.Count} laps averaged ({average} on average).";
            qualifyingBasis = $"Built on the session's fastest lap, L{fastestLap.LapNumber} · {TimeFormat.Lap(fastestLap.LapTimeMs)}.";
        }
        else
        {
            raceBasis = $"Built on the typical lap of {sessions.Count} sessions: {profiles.Count} laps averaged ({average} on average).";
            qualifyingBasis = $"Built on the fastest of their laps, L{fastestLap.LapNumber} of {SessionTypes.Name(fastest.Recording.SessionType)} #{fastest.Recording.Id} · {TimeFormat.Lap(fastestLap.LapTimeMs)}.";
        }

        return (new PlanInputs(model, race, profiles.Count, raceBasis, qualifying, qualifyingBasis), "");
    }
}
