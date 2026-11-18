using F1Telemetry.Core.Energy;
using F1Telemetry.Core.Models;
using F1Telemetry.Core.Setups;
using F1Telemetry.Protocol;
using F1Telemetry.Protocol.Lookups;

namespace F1Telemetry.Core.Hub;

/// <summary>
/// What the strategy planner learns from a session, kept small: its classified laps, their fuel and wear, and the wear
/// at the end. The stint analysis is run on them again when the hub is read, so the hub's strategy is fitted from the
/// same laps as the recording's was.
/// </summary>
public sealed record HubStrategyData(IReadOnlyList<LapRecord> Laps, IReadOnlyList<LapAggregate> Aggregates, WheelValues LatestWear);

/// <summary>What the energy planner learns from a session: the car model's sums, its typical lap and its fastest lap.</summary>
/// <param name="Model">The session's part of the ERS car model; null when it recorded no ERS data.</param>
/// <param name="Typical">The session's typical laps averaged (the race plan's lap); null when none could be profiled.</param>
/// <param name="Fastest">The session's fastest lap (the qualifying plan's lap); null when none could be profiled.</param>
public sealed record HubEnergyData(ErsModelStats? Model, LapProfileData? Typical, LapProfileData? Fastest, int FastestLapNumber, uint FastestLapMs);

/// <summary>
/// A session saved to the track hub: what the strategy, energy and setup views learned from it, which outlives its
/// recording. A session is identified by its recording and game session; saving it again replaces it.
/// </summary>
/// <param name="Id">The hub's id for it (not a recording id).</param>
/// <param name="RecordingId">The recording it was saved from; the recording may since have been deleted.</param>
/// <param name="RaceLength">The race length it counts for (see <see cref="RaceLengths"/>); 0 when not known.</param>
/// <param name="Laps">Timed laps in the session.</param>
/// <param name="BestLapMs">Its fastest valid lap; 0 when there is none.</param>
/// <param name="SetupRuns">Where it drove which setups, kept for the setup library.</param>
public sealed record HubSession(
    long Id,
    GameFormat Format,
    int TrackId,
    string TrackName,
    string SessionUid,
    long RecordingId,
    int SessionType,
    int RaceLength,
    DateTimeOffset StartTime,
    DateTimeOffset SavedAt,
    int Laps,
    uint BestLapMs,
    HubStrategyData Strategy,
    HubEnergyData Energy,
    IReadOnlyList<SetupRun> SetupRuns)
{
    /// <summary>Whether this is the saved copy of <paramref name="recording"/>.</summary>
    public bool IsOf(RecordingInfo recording) => RecordingId == recording.Id && SessionUid == recording.SessionUid;

    /// <summary>
    /// The session as a recording for the analyses, with the negative of its hub id (never a recording's id) and the
    /// race length it counts for.
    /// </summary>
    public RecordingInfo AsRecording() =>
        new(-Id, SessionUid, $"Saved in the hub {SavedAt.LocalDateTime:d MMM yyyy}", TrackId, TrackName, SessionType, Format, StartTime, StartTime)
        {
            SessionLength = RaceLength,
        };
}

/// <summary>The laps the ERS planner is given from a track's sessions: their typical laps averaged, and the fastest lap.</summary>
/// <param name="RaceLaps">How many typical laps <paramref name="Race"/> averages.</param>
public sealed record HubPlanLaps(LapProfile Race, int RaceLaps, string RaceBasis, LapProfile Qualifying, string QualifyingBasis);

/// <summary>
/// A session at a track for the strategy and energy views: a recording not saved to the hub, a recording and its hub
/// copy, or a hub session whose recording was deleted.
/// </summary>
/// <param name="RaceLength">The race length it counts for: the hub copy's, else inferred (see <see cref="TrackHub.RaceLengthOf"/>).</param>
public sealed record TrackSession(RecordingInfo? Recording, HubSession? Saved, int RaceLength)
{
    public bool IsSaved => Saved is not null;

    public bool HasRecording => Recording is not null;

    public int SessionType => Recording?.SessionType ?? Saved!.SessionType;

    public DateTimeOffset StartTime => Recording?.StartTime ?? Saved!.StartTime;

    /// <summary>What the analyses know it by: the hub copy when there is one (see <see cref="HubSession.AsRecording"/>).</summary>
    public RecordingInfo Info => Saved?.AsRecording() ?? Recording!;
}

/// <summary>Rules the track hub shares between its views.</summary>
public static class TrackHub
{
    /// <summary>A practice or qualifying session counts for a race at the track recorded within this long of it.</summary>
    public static readonly TimeSpan WeekendWindow = TimeSpan.FromHours(12);

    /// <summary>
    /// The race length a recording counts for: a race's own; for practice and qualifying, that of the nearest race at the
    /// same track and format within <see cref="WeekendWindow"/> (the weekend it belongs to), since the game scales wear
    /// to the race. 0 when not known.
    /// </summary>
    public static int RaceLengthOf(RecordingInfo recording, IEnumerable<RecordingInfo> atTrack)
    {
        if (SessionTypes.IsRace(recording.SessionType))
        {
            return RaceLengths.IsKnown(recording.SessionLength) ? recording.SessionLength : RaceLengths.Unknown;
        }

        var race = atTrack
            .Where(r => r.Id != recording.Id && r.Format == recording.Format && r.TrackId == recording.TrackId
                        && SessionTypes.IsRace(r.SessionType) && RaceLengths.IsKnown(r.SessionLength))
            .Select(r => (Race: r, Gap: (r.StartTime - recording.StartTime).Duration()))
            .Where(r => r.Gap <= WeekendWindow)
            .OrderBy(r => r.Gap)
            .Select(r => r.Race)
            .FirstOrDefault();
        return race?.SessionLength ?? RaceLengths.Unknown;
    }
}
