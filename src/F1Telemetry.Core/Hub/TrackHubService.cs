using System.Collections.Concurrent;
using F1Telemetry.Core.Energy;
using F1Telemetry.Core.Formatting;
using F1Telemetry.Core.Models;
using F1Telemetry.Core.Recording;
using F1Telemetry.Core.Setups;
using F1Telemetry.Protocol;
using F1Telemetry.Protocol.Lookups;

namespace F1Telemetry.Core.Hub;

/// <summary>
/// The track hub: saves what the strategy, energy and setup views learned from a session, so its recording can be
/// deleted, and gives those views the track's knowledge as the hub's sessions plus every recording not saved to it.
/// Nothing is saved without being asked.
/// </summary>
public sealed class TrackHubService(ITelemetryStore store, SessionAnalysisService analysis)
{
    // What a finished recording gives the energy planner, by recording id: read once (its samples are the heavy part).
    private readonly ConcurrentDictionary<long, (RecordingInfo Recording, HubEnergyData Energy)> _energy = new();
    private readonly ConcurrentDictionary<long, (RecordingInfo Recording, ErsModelStats? Stats)> _stats = new();

    /// <summary>The hub changed at a track (sessions saved, removed or relabelled).</summary>
    public event Action<GameFormat, int>? Changed;

    public Task<IReadOnlyList<TrackSession>> SessionsAsync(GameFormat format, int trackId) => analysis.LoadTrackSessionsAsync(format, trackId);

    /// <summary>Tracks with a session saved to the hub or a recording: (format, track id, name), by name.</summary>
    public async Task<IReadOnlyList<(GameFormat Format, int TrackId, string Name)>> TracksAsync()
    {
        var recorded = (await store.GetRecordingsAsync()).Where(r => r.TrackId >= 0).Select(r => (r.Format, r.TrackId, Name: r.TrackName));
        var saved = (await store.GetHubSessionsAsync()).Select(h => (h.Format, h.TrackId, Name: h.TrackName));
        return [.. recorded.Concat(saved).DistinctBy(t => (t.Format, t.TrackId)).OrderBy(t => t.Name).ThenBy(t => t.Format)];
    }

    /// <summary>
    /// Saves the recordings of <paramref name="sessions"/> to the hub as they are now (a session saved before is replaced),
    /// each counting for its <see cref="TrackSession.RaceLength"/>. Returns how many were saved.
    /// </summary>
    public async Task<int> SaveAsync(IReadOnlyList<TrackSession> sessions)
    {
        await store.FlushAsync();
        await store.GetSetupLibraryAsync(); // adds the setups the sessions drove, so their runs can name them
        var runs = await store.GetSetupRunsAsync();
        var snapshots = new List<HubSession>();
        foreach (var session in sessions.Where(s => s.Recording is not null))
        {
            var recording = session.Recording!;
            if (await SnapshotAsync(recording, session.RaceLength, [.. runs.Where(r => r.RecordingId == recording.Id)]) is { } snapshot)
            {
                snapshots.Add(snapshot);
            }
        }

        if (snapshots.Count == 0)
        {
            return 0;
        }

        await store.SaveHubSessionsAsync(snapshots);
        foreach (var track in snapshots.Select(s => (s.Format, s.TrackId)).Distinct())
        {
            Changed?.Invoke(track.Format, track.TrackId);
        }

        return snapshots.Count;
    }

    /// <summary>Takes sessions out of the hub; their recordings, where there still are any, become unsaved.</summary>
    public async Task RemoveAsync(IReadOnlyList<HubSession> sessions)
    {
        await store.RemoveHubSessionsAsync([.. sessions.Select(s => s.Id)]);
        foreach (var track in sessions.Select(s => (s.Format, s.TrackId)).Distinct())
        {
            Changed?.Invoke(track.Format, track.TrackId);
        }
    }

    public async Task SetRaceLengthAsync(HubSession session, int raceLength)
    {
        await store.SetHubRaceLengthAsync(session.Id, raceLength);
        Changed?.Invoke(session.Format, session.TrackId);
    }

    /// <summary>
    /// Corrects what kind of session this was (the game's own type unless it sent none, or the wrong one): its recording,
    /// where there is one, and its hub copy, where it was saved. The analyses read it again (a race keeps lap 1 out of
    /// its pace, practice counts for its weekend's race).
    /// </summary>
    public async Task SetSessionTypeAsync(TrackSession session, int sessionType)
    {
        if (session.Recording is { } recording)
        {
            store.UpdateRecording(recording.Id, sessionType: sessionType);
            await store.FlushAsync();
            analysis.Forget(recording.Id);
            Forget(recording.Id);
        }

        if (session.Saved is { } saved)
        {
            await store.SetHubSessionTypeAsync(saved.Id, sessionType);
        }

        var (format, trackId) = session.Recording is { } r ? (r.Format, r.TrackId) : (session.Saved!.Format, session.Saved.TrackId);
        Changed?.Invoke(format, trackId);
    }

    /// <summary>
    /// What the hub would keep of <paramref name="recording"/> as it is now; null when it has no laps. Not saved: see
    /// <see cref="SaveAsync"/>.
    /// </summary>
    /// <param name="runs">The recording's setup runs (read from the store when null).</param>
    public async Task<HubSession?> SnapshotAsync(RecordingInfo recording, int raceLength, IReadOnlyList<SetupRun>? runs = null)
    {
        var laps = await analysis.GetClassifiedLapsAsync(recording);
        if (laps.Count == 0)
        {
            return null;
        }

        var race = await analysis.LoadRaceAsync(recording);
        var strategy = race is not null
            ? new HubStrategyData(race.Laps, race.Aggregates, race.Analysis.CurrentWear)
            : new HubStrategyData(laps, await store.GetLapAggregatesAsync(recording.Id), await store.GetLatestWearAsync(recording.Id));
        runs ??= [.. (await store.GetSetupRunsAsync()).Where(r => r.RecordingId == recording.Id)];
        var timed = laps.Where(l => l.HasTime).ToList();
        return new HubSession(0, recording.Format, recording.TrackId, recording.TrackName, recording.SessionUid, recording.Id, recording.SessionType,
            raceLength, recording.StartTime, DateTimeOffset.UtcNow, timed.Count,
            timed.Where(l => l.IsValid).Select(l => l.LapTimeMs).DefaultIfEmpty().Min(),
            strategy, await EnergyAsync(recording, laps), runs)
        {
            WeekendId = recording.WeekendId,
        };
    }

    /// <summary>
    /// The ERS car model of a track: the hub's sessions' sums and every unsaved recording's samples, or those
    /// <paramref name="include"/> picks.
    /// </summary>
    public async Task<ErsCarModel> BuildErsModelAsync(GameFormat format, int trackId, Func<TrackSession, bool>? include = null) =>
        await BuildErsModelAsync(format, trackId, [.. (await SessionsAsync(format, trackId)).Where(s => include is null || include(s))]);

    /// <summary>The ERS car model of these sessions: a saved one's sums as saved, an unsaved one's from its samples.</summary>
    public async Task<ErsCarModel> BuildErsModelAsync(GameFormat format, int trackId, IReadOnlyList<TrackSession> sessions)
    {
        var parts = sessions.Where(s => s.Saved?.Energy.Model is not null).Select(s => s.Saved!.Energy.Model!).ToList();
        parts.AddRange((await StatsAsync([.. sessions.Where(s => !s.IsSaved && s.HasRecording).Select(s => s.Recording!)])).Where(s => s is not null)!);
        return await Task.Run(() => ErsModelStats.Merge(parts).Build(format, trackId));
    }

    /// <summary>
    /// The energy planner's laps over a track's sessions (the hub's and the unsaved recordings', or those
    /// <paramref name="include"/> picks): race plans on all their typical laps averaged, qualifying plans on the fastest
    /// lap among them. Null, with the reason, when they can't be planned.
    /// </summary>
    public async Task<(HubPlanLaps? Laps, string Unavailable)> PlanLapsAsync(GameFormat format, int trackId, ErsCarModel model,
        Func<TrackSession, bool>? include = null) =>
        await PlanLapsAsync(model, [.. (await SessionsAsync(format, trackId)).Where(s => include is null || include(s))]);

    /// <summary>The energy planner's laps over these sessions (see the overload above).</summary>
    public async Task<(HubPlanLaps? Laps, string Unavailable)> PlanLapsAsync(ErsCarModel model, IReadOnlyList<TrackSession> sessions)
    {
        if (model is not { HasPowerData: true })
        {
            return (null, "No plan: no session at this track has ICE and MGU-K power data, so there is no car model to plan with.");
        }

        if (model.StraightLine.Count == 0 || model.Deploy.Curves.Count == 0)
        {
            return (null, "No plan yet: the car model needs a straight-line fit and a deploy map first. Record a few more laps at this track.");
        }

        var typical = new List<LapProfile>();
        (LapProfile Profile, uint Ms, string Label)? fastest = null;
        foreach (var session in sessions)
        {
            HubEnergyData? energy = session.Saved?.Energy;
            if (energy is null && session.Recording is { } recording)
            {
                energy = await EnergyAsync(recording, await analysis.GetClassifiedLapsAsync(recording));
            }

            if (energy is null)
            {
                continue;
            }

            if (energy.Typical is { } t && LapProfile.FromData(t) is { } profile)
            {
                typical.Add(profile);
            }

            if (energy.Fastest is { } f && energy.FastestLapMs > 0 && (fastest is null || energy.FastestLapMs < fastest.Value.Ms) && LapProfile.FromData(f) is { } lap)
            {
                fastest = (lap, energy.FastestLapMs, $"L{energy.FastestLapNumber} of {SessionTypes.Name(session.SessionType)} {session.StartTime.LocalDateTime:d MMM}");
            }
        }

        var race = LapProfile.Average(typical);
        if (race is null || fastest is not { } quickest)
        {
            return (null, "No plan: these sessions have no timed lap with ICE and MGU-K power data to build one on.");
        }

        var saved = sessions.Count(s => s.IsSaved);
        var from = sessions.Count == 1 ? "one session" : $"{sessions.Count} sessions" + (saved > 0 ? $" ({saved} in the hub)" : "");
        var raceBasis = $"Built on the typical lap of {from}: {race.LapCount} laps averaged ({TimeFormat.Lap(race.ActualSeconds * 1000)} on average).";
        var qualifyingBasis = $"Built on the fastest of their laps, {quickest.Label} · {TimeFormat.Lap(quickest.Ms)}.";
        return (new HubPlanLaps(race, race.LapCount, raceBasis, quickest.Profile, qualifyingBasis), "");
    }

    /// <summary>What a recording gives the energy planner: its car-model sums, its typical laps averaged, its fastest lap.</summary>
    private async Task<HubEnergyData> EnergyAsync(RecordingInfo recording, IReadOnlyList<LapRecord> laps)
    {
        if (recording.EndTime is not null && _energy.TryGetValue(recording.Id, out var kept) && kept.Recording == recording)
        {
            return kept.Energy;
        }

        var stats = (await StatsAsync([recording]))[0];
        var representative = SessionLaps.Representative(laps);
        var fastest = SessionLaps.Fastest(laps);
        var profiles = new List<LapProfile>();
        LapProfile? fastestProfile = null;
        foreach (var lap in representative.Append(fastest).OfType<LapRecord>().DistinctBy(l => l.LapNumber))
        {
            var samples = await store.GetLapSamplesAsync(recording.Id, lap.LapNumber);
            var profile = await Task.Run(() => LapProfile.From(samples, recording.Format, lap.LapTimeMs / 1000.0));
            if (profile is null)
            {
                continue;
            }

            if (representative.Any(r => r.LapNumber == lap.LapNumber))
            {
                profiles.Add(profile);
            }

            if (lap.LapNumber == fastest?.LapNumber)
            {
                fastestProfile = profile.WithRunUp(await SessionLaps.RunUpStoreAsync(store, recording.Id, samples));
            }
        }

        var energy = new HubEnergyData(stats, LapProfile.Average(profiles)?.ToData(), fastestProfile?.ToData(),
            fastestProfile is null ? 0 : fastest!.LapNumber, fastestProfile is null ? 0 : fastest!.LapTimeMs);
        if (recording.EndTime is not null)
        {
            _energy[recording.Id] = (recording, energy);
        }

        return energy;
    }

    /// <summary>Each recording's car-model sums (null without samples), in order; the samples are read once for them all.</summary>
    private async Task<IReadOnlyList<ErsModelStats?>> StatsAsync(IReadOnlyList<RecordingInfo> recordings)
    {
        var result = new ErsModelStats?[recordings.Count];
        var missing = new List<int>();
        for (var i = 0; i < recordings.Count; i++)
        {
            if (recordings[i].EndTime is not null && _stats.TryGetValue(recordings[i].Id, out var kept) && kept.Recording == recordings[i])
            {
                result[i] = kept.Stats;
            }
            else
            {
                missing.Add(i);
            }
        }

        foreach (var track in missing.GroupBy(i => (recordings[i].Format, recordings[i].TrackId)))
        {
            var samples = await store.GetModelSamplesAsync(track.Key.Format, track.Key.TrackId);
            var byRecording = samples.GroupBy(s => s.RecordingId).ToDictionary(g => g.Key, g => (IReadOnlyList<ModelSample>)[.. g]);
            foreach (var i in track)
            {
                var recording = recordings[i];
                var stats = byRecording.TryGetValue(recording.Id, out var own) && own.Count > 0
                    ? await Task.Run(() => ErsModelStats.From(recording.Format, own))
                    : null;
                result[i] = stats;
                if (recording.EndTime is not null)
                {
                    _stats[recording.Id] = (recording, stats);
                }
            }
        }

        return result;
    }

    /// <summary>Drops what is kept of a deleted recording.</summary>
    public void Forget(long recordingId)
    {
        _energy.TryRemove(recordingId, out _);
        _stats.TryRemove(recordingId, out _);
    }
}
