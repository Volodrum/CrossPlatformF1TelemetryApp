using F1Telemetry.Core.Analytics;
using F1Telemetry.Core.Engine;
using F1Telemetry.Core.Models;

namespace F1Telemetry.Core.Recording;

/// <summary>Read-side facade: loads a recording from the store and runs classification and stint analysis.</summary>
public sealed class SessionAnalysisService(ITelemetryStore store, StrategyOptions? options = null)
{
    public async Task<IReadOnlyList<LapRecord>> GetClassifiedLapsAsync(RecordingInfo recording, CancellationToken ct = default)
    {
        var laps = await store.GetLapsAsync(recording.Id, ct);
        return LapClassifier.Classify(laps, recording.SessionType);
    }

    public async Task<SessionAnalysis?> AnalyzeAsync(RecordingInfo recording, IReadOnlyList<LapRecord>? classifiedLaps = null, CancellationToken ct = default)
    {
        classifiedLaps ??= await GetClassifiedLapsAsync(recording, ct);
        if (classifiedLaps.Count == 0)
        {
            return null;
        }

        var aggregates = await store.GetLapAggregatesAsync(recording.Id, ct);
        var wear = await store.GetLatestWearAsync(recording.Id, ct);
        return StintAnalyzer.Analyze(classifiedLaps, aggregates, wear, recording.SessionType, options);
    }

    /// <summary>Everything <see cref="RaceComparison"/> needs about one recording; null when it has no laps.</summary>
    public async Task<RaceInput?> LoadRaceAsync(RecordingInfo recording, CancellationToken ct = default)
    {
        var laps = await GetClassifiedLapsAsync(recording, ct);
        if (laps.Count == 0)
        {
            return null;
        }

        var aggregates = await store.GetLapAggregatesAsync(recording.Id, ct);
        var wear = await store.GetLatestWearAsync(recording.Id, ct);
        var analysis = StintAnalyzer.Analyze(laps, aggregates, wear, recording.SessionType, options);
        return analysis is null ? null : new RaceInput(recording, laps, aggregates, analysis);
    }

    public double DefaultFuelEffect => (options ?? new StrategyOptions()).FuelEffectSecondsPerKg;

    public async Task<RaceComparisonResult?> CompareAsync(RecordingInfo a, RecordingInfo b, CancellationToken ct = default)
    {
        var raceA = await LoadRaceAsync(a, ct);
        var raceB = await LoadRaceAsync(b, ct);
        return raceA is null || raceB is null ? null : RaceComparison.Compare(raceA, raceB, DefaultFuelEffect);
    }
}
