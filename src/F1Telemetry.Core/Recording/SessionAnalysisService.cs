using System.Collections.Concurrent;
using F1Telemetry.Core.Analytics;
using F1Telemetry.Core.Engine;
using F1Telemetry.Core.Models;
using F1Telemetry.Protocol;

namespace F1Telemetry.Core.Recording;

/// <summary>A track's compound library and the data guide built on it.</summary>
public sealed record TrackKnowledge(CompoundLibrary Library, DataGuide Guide);

/// <summary>
/// Read-side facade: loads a recording from the store and runs classification and stint analysis.
/// <para>
/// A finished recording never changes (nothing is written to it once it has an end time, and that end time is written
/// after everything else, in order), so its analysis is kept: the compound library, which reads every recording at a
/// track, a comparison, and the session on screen then read the database once per recording instead of on every build.
/// A live recording is read afresh each time.
/// </para>
/// </summary>
public sealed class SessionAnalysisService(ITelemetryStore store, StrategyOptions? options = null)
{
    // Kept apart: the laps alone are a light read that many views need; the analysis also reads the per-lap aggregates.
    private readonly ConcurrentDictionary<long, (RecordingInfo Recording, IReadOnlyList<LapRecord> Laps)> _finishedLaps = new();
    private readonly ConcurrentDictionary<long, (RecordingInfo Recording, RaceInput? Race)> _finished = new();

    public async Task<IReadOnlyList<LapRecord>> GetClassifiedLapsAsync(RecordingInfo recording, CancellationToken ct = default)
    {
        var finished = recording.EndTime is not null;
        if (finished && _finishedLaps.TryGetValue(recording.Id, out var kept) && kept.Recording == recording)
        {
            return kept.Laps;
        }

        var laps = LapClassifier.Classify(await store.GetLapsAsync(recording.Id, ct), recording.SessionType);
        if (finished)
        {
            _finishedLaps[recording.Id] = (recording, laps);
        }

        return laps;
    }

    public async Task<SessionAnalysis?> AnalyzeAsync(RecordingInfo recording, IReadOnlyList<LapRecord>? classifiedLaps = null, CancellationToken ct = default)
    {
        if (recording.EndTime is not null)
        {
            return (await LoadRaceAsync(recording, ct))?.Analysis;
        }

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
        var finished = recording.EndTime is not null;
        if (finished && _finished.TryGetValue(recording.Id, out var kept) && kept.Recording == recording)
        {
            return kept.Race;
        }

        var race = await ReadRaceAsync(recording, ct);
        if (finished)
        {
            _finished[recording.Id] = (recording, race);
        }

        return race;
    }

    private async Task<RaceInput?> ReadRaceAsync(RecordingInfo recording, CancellationToken ct)
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

    /// <summary>Drops what is kept of a recording (it was deleted).</summary>
    public void Forget(long recordingId)
    {
        _finishedLaps.TryRemove(recordingId, out _);
        _finished.TryRemove(recordingId, out _);
    }

    public double DefaultFuelEffect => (options ?? new StrategyOptions()).FuelEffectSecondsPerKg;

    /// <summary>Every analysed recording at a track in one game format: what the compound library is built from.</summary>
    public async Task<IReadOnlyList<RaceInput>> LoadTrackAsync(GameFormat format, int trackId, CancellationToken ct = default)
    {
        var sessions = new List<RaceInput>();
        foreach (var recording in await store.GetRecordingsAsync(ct))
        {
            if (recording.TrackId == trackId && recording.Format == format && await LoadRaceAsync(recording, ct) is { } race)
            {
                sessions.Add(race);
            }
        }

        return sessions;
    }

    /// <summary>
    /// The compound library of <paramref name="anchor"/>'s track and game format, from every recording there, with paces
    /// in <paramref name="anchor"/>'s conditions (see <see cref="CompoundLibraryBuilder"/>).
    /// </summary>
    public async Task<CompoundLibrary> BuildCompoundLibraryAsync(RecordingInfo anchor, CancellationToken ct = default) =>
        CompoundLibraryBuilder.Build(await LoadTrackAsync(anchor.Format, anchor.TrackId, ct), anchor.Id, options);

    /// <summary>
    /// The compound library of a track with the data guide for it: what the plans know there and what to drive next.
    /// </summary>
    /// <param name="anchorRecordingId">
    /// The session whose conditions the paces are in; null (the session being driven now has no recording, say) takes the
    /// session with the most laps.
    /// </param>
    public async Task<TrackKnowledge> BuildTrackKnowledgeAsync(GameFormat format, int trackId, long? anchorRecordingId, CancellationToken ct = default)
    {
        var sessions = await LoadTrackAsync(format, trackId, ct);
        var library = CompoundLibraryBuilder.Build(sessions, anchorRecordingId, options);
        return new TrackKnowledge(library, DataGuideBuilder.Build(library, sessions, options));
    }

    public async Task<RaceComparisonResult?> CompareAsync(RecordingInfo a, RecordingInfo b, CancellationToken ct = default)
    {
        var raceA = await LoadRaceAsync(a, ct);
        var raceB = await LoadRaceAsync(b, ct);
        return raceA is null || raceB is null ? null : RaceComparison.Compare(raceA, raceB, DefaultFuelEffect);
    }
}
