using System.Collections.Concurrent;
using F1Telemetry.Core.Analytics;
using F1Telemetry.Core.Engine;
using F1Telemetry.Core.Hub;
using F1Telemetry.Core.Models;
using F1Telemetry.Protocol;
using F1Telemetry.Protocol.Lookups;

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

    // Hub sessions analysed again, by hub id: a session only changes when it is saved again.
    private readonly ConcurrentDictionary<long, (DateTimeOffset SavedAt, int RaceLength, int SessionType, RaceInput? Race)> _restored = new();

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

    /// <summary>
    /// Every session at a track in one game format: each recording (with its copy in the track hub when it was saved),
    /// then each hub session whose recording is gone. Oldest first.
    /// </summary>
    public async Task<IReadOnlyList<TrackSession>> LoadTrackSessionsAsync(GameFormat format, int trackId, CancellationToken ct = default)
    {
        var recordings = (await store.GetRecordingsAsync(ct)).Where(r => r.TrackId == trackId && r.Format == format).ToList();
        var saved = await store.GetHubSessionsAsync(format, trackId, ct);

        // A race kept only in the hub still tells its weekend's practice and qualifying what race they were for.
        var known = recordings.Concat(saved.Where(h => !recordings.Any(h.IsOf)).Select(h => h.AsRecording())).ToList();
        var sessions = new List<TrackSession>();
        foreach (var recording in recordings)
        {
            var copy = saved.FirstOrDefault(h => h.IsOf(recording));
            sessions.Add(new TrackSession(recording, copy, Length(copy, recording)));
        }

        sessions.AddRange(saved.Where(h => !recordings.Any(h.IsOf)).Select(h => new TrackSession(null, h, Length(h, null))));
        return [.. sessions.OrderBy(s => s.StartTime)];

        // A saved session counts for the length it was saved with. One saved before its race was known (practice saved
        // before the race was run) takes its weekend's race once there is one.
        int Length(HubSession? copy, RecordingInfo? recording)
        {
            if (copy is not null && (RaceLengths.IsKnown(copy.RaceLength) || SessionTypes.IsRace(copy.SessionType)))
            {
                return copy.RaceLength;
            }

            var info = copy?.AsRecording() is { } r ? r with { SessionLength = 0 } : recording!;
            return TrackHub.RaceLengthOf(info, known);
        }
    }

    /// <summary>
    /// A session's laps and analysis: a saved session's from what the hub kept (whether or not its recording is still
    /// there: the hub is what it was saved as), an unsaved one's from its recording. Null when it has no laps.
    /// </summary>
    public async Task<RaceInput?> LoadRaceAsync(TrackSession session, CancellationToken ct = default)
    {
        if (session.Saved is { } saved)
        {
            return Restore(saved) is { } kept ? kept with { RaceLength = session.RaceLength } : null;
        }

        return await LoadRaceAsync(session.Recording!, ct) is { } race ? race with { RaceLength = session.RaceLength } : null;
    }

    /// <summary>The laps and analysis a hub session kept, analysed again as its recording was; null when it kept no laps.</summary>
    public RaceInput? Restore(HubSession saved)
    {
        if (_restored.TryGetValue(saved.Id, out var kept) && kept.SavedAt == saved.SavedAt && kept.RaceLength == saved.RaceLength
            && kept.SessionType == saved.SessionType)
        {
            return kept.Race;
        }

        var data = saved.Strategy;
        var analysis = data.Laps.Count == 0 ? null : StintAnalyzer.Analyze(data.Laps, data.Aggregates, data.LatestWear, saved.SessionType, options);
        var race = analysis is null ? null : new RaceInput(saved.AsRecording(), data.Laps, data.Aggregates, analysis) { RaceLength = saved.RaceLength };
        _restored[saved.Id] = (saved.SavedAt, saved.RaceLength, saved.SessionType, race);
        return race;
    }

    /// <summary>
    /// Every analysed session at a track in one game format, saved and unsaved (see <see cref="LoadTrackSessionsAsync"/>):
    /// what the compound library is built from. <paramref name="include"/> picks some of them.
    /// </summary>
    public async Task<IReadOnlyList<RaceInput>> LoadTrackAsync(GameFormat format, int trackId, Func<TrackSession, bool>? include = null, CancellationToken ct = default)
    {
        var sessions = new List<RaceInput>();
        foreach (var session in await LoadTrackSessionsAsync(format, trackId, ct))
        {
            if ((include is null || include(session)) && await LoadRaceAsync(session, ct) is { } race)
            {
                sessions.Add(race);
            }
        }

        return sessions;
    }

    /// <summary>
    /// The compound library of <paramref name="anchor"/>'s track and game format, from every session there, with paces
    /// in <paramref name="anchor"/>'s conditions (see <see cref="CompoundLibraryBuilder"/>).
    /// </summary>
    public async Task<CompoundLibrary> BuildCompoundLibraryAsync(RecordingInfo anchor, CancellationToken ct = default) =>
        (await BuildTrackKnowledgeAsync(anchor.Format, anchor.TrackId, anchor.Id, ct: ct)).Library;

    /// <summary>
    /// The compound library of a track with the data guide for it: what the plans know there and what to drive next.
    /// Built on the track hub's sessions and every recording there not saved to it.
    /// </summary>
    /// <param name="anchorRecordingId">
    /// The session whose conditions the paces are in: a recording's id, or a hub session's as its
    /// <see cref="HubSession.AsRecording"/> gives it. Null (the session being driven now has no recording, say) takes the
    /// session with the most laps.
    /// </param>
    /// <param name="raceLength">The race length to plan for (see <see cref="CompoundLibraryBuilder.Build"/>); 0 pools them.</param>
    public async Task<TrackKnowledge> BuildTrackKnowledgeAsync(GameFormat format, int trackId, long? anchorRecordingId, int raceLength = 0,
        Func<TrackSession, bool>? include = null, CancellationToken ct = default)
    {
        var all = await LoadTrackSessionsAsync(format, trackId, ct);
        var sessions = new List<RaceInput>();
        foreach (var session in all.Where(s => include is null || include(s)))
        {
            if (await LoadRaceAsync(session, ct) is { } race)
            {
                sessions.Add(race);
            }
        }

        // A saved recording is analysed as its hub copy: anchor on that.
        var anchor = all.FirstOrDefault(s => s.Recording?.Id == anchorRecordingId && s.Saved is not null) is { Saved: { } copy } ? -copy.Id : anchorRecordingId;
        return BuildTrackKnowledge(sessions, anchor, raceLength);
    }

    /// <summary>The compound library and data guide of these sessions (see <see cref="BuildTrackKnowledgeAsync"/>).</summary>
    public TrackKnowledge BuildTrackKnowledge(IReadOnlyList<RaceInput> sessions, long? anchorRecordingId, int raceLength = 0)
    {
        var library = CompoundLibraryBuilder.Build(sessions, anchorRecordingId, options, raceLength);
        return new TrackKnowledge(library, DataGuideBuilder.Build(library, sessions, options));
    }

    /// <summary>The fuel these sessions burn, by what each run was driven as (see <see cref="FuelModelBuilder"/>).</summary>
    public FuelModel BuildFuelModel(IReadOnlyList<RaceInput> sessions, int raceLength = 0) => FuelModelBuilder.Build(sessions, raceLength, options);

    public async Task<RaceComparisonResult?> CompareAsync(RecordingInfo a, RecordingInfo b, CancellationToken ct = default)
    {
        var raceA = await LoadRaceAsync(a, ct);
        var raceB = await LoadRaceAsync(b, ct);
        return raceA is null || raceB is null ? null : RaceComparison.Compare(raceA, raceB, DefaultFuelEffect);
    }
}
