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
}
