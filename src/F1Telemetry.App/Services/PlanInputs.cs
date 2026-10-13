using F1Telemetry.Core.Energy;
using F1Telemetry.Core.Formatting;
using F1Telemetry.Core.Models;

namespace F1Telemetry.App.Services;

/// <summary>What a session gives the planner: the car model and the lap each kind of plan is built on.</summary>
/// <param name="Race">The session's typical lap (its laps averaged): a race plan must hold for every one of them.</param>
/// <param name="RaceLaps">How many typical laps <paramref name="Race"/> averages.</param>
/// <param name="Qualifying">The session's fastest lap: one push lap.</param>
public sealed record PlanInputs(ErsCarModel Model, LapProfile Race, int RaceLaps, string RaceBasis, LapProfile Qualifying, string QualifyingBasis)
{
    private const string Reimport = "Recordings made before the app stored engine power can get it from their raw capture: close the app and run "
        + "f1tel reimport --db <telemetry.duckdb> <capture.f1rec> --replace (see the README). New recordings have it.";

    /// <summary>
    /// The laps a session's plans are built on, as the energy tab shows them: race plans on its typical laps averaged,
    /// qualifying plans on its fastest lap. Null, with the reason, when the session can't be planned.
    /// <paramref name="fastestSamples"/> saves loading the fastest lap again when the caller has it.
    /// </summary>
    public static async Task<(PlanInputs? Inputs, string Unavailable)> LoadAsync(TelemetryRuntime runtime, RecordingInfo recording, ErsCarModel? model,
        IReadOnlyList<LapRecord> representative, LapRecord? fastest, IReadOnlyList<TelemetrySample>? fastestSamples = null)
    {
        if (model is not { HasPowerData: true })
        {
            return (null, "No plan: no lap at this track has ICE and MGU-K power data, so there is no car model to plan with. " + Reimport);
        }

        if (model.StraightLine.Count == 0 || model.Deploy.Curves.Count == 0)
        {
            return (null, "No plan yet: the car model needs a straight-line fit and a deploy map first. Record a few more laps at this track.");
        }

        if (fastest is null)
        {
            return (null, "No plan: this session has no timed lap to build one on.");
        }

        fastestSamples ??= await runtime.Store.GetLapSamplesAsync(recording.Id, fastest.LapNumber);
        var profiles = new List<(LapRecord Lap, LapProfile Profile)>();
        foreach (var lap in representative)
        {
            var samples = lap.LapNumber == fastest.LapNumber ? fastestSamples : await runtime.Store.GetLapSamplesAsync(recording.Id, lap.LapNumber);
            if (await Task.Run(() => LapProfile.From(samples, recording.Format, lap.LapTimeMs / 1000.0)) is { } profile)
            {
                profiles.Add((lap, profile));
            }
        }

        var qualifying = LapProfile.From(fastestSamples, recording.Format, fastest.LapTimeMs / 1000.0);
        var race = await Task.Run(() => LapProfile.Average([.. profiles.Select(p => p.Profile)]));
        if (race is null || qualifying is null)
        {
            return (null, "No plan: this session has no ICE and MGU-K power data. " + Reimport);
        }

        var average = TimeFormat.Lap(profiles.Average(p => (double)p.Lap.LapTimeMs));
        var raceBasis = profiles.Count == 1
            ? $"Built on the session's only typical lap, L{profiles[0].Lap.LapNumber} · {average}."
            : $"Built on the session's typical lap: {profiles.Count} laps averaged ({average} on average).";
        var qualifyingBasis = $"Built on the session's fastest lap, L{fastest.LapNumber} · {TimeFormat.Lap(fastest.LapTimeMs)}.";
        return (new PlanInputs(model, race, profiles.Count, raceBasis, qualifying, qualifyingBasis), "");
    }
}
