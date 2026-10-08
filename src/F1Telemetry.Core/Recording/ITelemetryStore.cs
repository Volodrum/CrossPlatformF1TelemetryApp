using F1Telemetry.Core.Energy;
using F1Telemetry.Core.Models;
using F1Telemetry.Protocol;
using F1Telemetry.Core.Setups;

namespace F1Telemetry.Core.Recording;

/// <summary>
/// Persistence port. Write methods are non-blocking (enqueue onto a single background writer, preserving
/// order) so they can be called from the 60 Hz ingest thread; reads are asynchronous.
/// </summary>
public interface ITelemetryStore : IAsyncDisposable
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>Allocates an id synchronously and enqueues the insert.</summary>
    long BeginRecording(NewRecording recording);

    void EndRecording(long recordingId);

    void UpdateRecording(long recordingId, string? sessionUid = null, int? trackId = null, string? trackName = null, int? sessionType = null,
        DateTimeOffset? startTime = null, DateTimeOffset? endTime = null);

    void AppendSample(TelemetrySample sample);

    void UpsertLap(long recordingId, LapRecord lap);

    /// <summary>Records that the player drove <paramref name="setup"/> from its lap and session time on.</summary>
    void AppendSetup(long recordingId, SetupChange setup);

    /// <summary>Completes once every write enqueued so far is durable.</summary>
    Task FlushAsync(CancellationToken cancellationToken = default);

    Task DeleteRecordingAsync(long recordingId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RecordingInfo>> GetRecordingsAsync(CancellationToken cancellationToken = default);

    Task<RecordingInfo?> GetRecordingAsync(long recordingId, CancellationToken cancellationToken = default);

    /// <summary>Laps that have telemetry and/or a summary, ascending, with stored (unclassified) lap types.</summary>
    Task<IReadOnlyList<LapRecord>> GetLapsAsync(long recordingId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TelemetrySample>> GetLapSamplesAsync(long recordingId, int lapNumber, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every sample of a recording in session-time order, with only lap, time, distance, speed, pedals and the energy
    /// fields (battery, deploy mode, per-lap harvest and deploy counters, harvest limit, MGU-K power) filled in.
    /// </summary>
    Task<IReadOnlyList<TelemetrySample>> GetEnergySamplesAsync(long recordingId, CancellationToken cancellationToken = default);

    /// <summary>Every sample of every recording at a track in one game format, for the ERS car model; by recording, then time.</summary>
    Task<IReadOnlyList<ModelSample>> GetModelSamplesAsync(GameFormat format, int trackId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LapAggregate>> GetLapAggregatesAsync(long recordingId, CancellationToken cancellationToken = default);

    Task<WheelValues> GetLatestWearAsync(long recordingId, CancellationToken cancellationToken = default);

    /// <summary>The setups driven in a recording, in the order they arrived.</summary>
    Task<IReadOnlyList<SetupChange>> GetSetupsAsync(long recordingId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The setup library, after adding every setup driven in a recording that it doesn't have yet (one entry per track,
    /// game format and setup, fuel load aside; setups loaded in the garage but never driven are left out).
    /// </summary>
    Task<IReadOnlyList<SetupEntry>> GetSetupLibraryAsync(CancellationToken cancellationToken = default);

    /// <summary>Where each library setup was driven, newest recording first.</summary>
    Task<IReadOnlyList<SetupRun>> GetSetupRunsAsync(CancellationToken cancellationToken = default);

    /// <summary>Renames (an empty name goes back to the automatic one), annotates, stars or removes a library setup.</summary>
    void UpdateSetup(long setupId, string? name = null, string? notes = null, bool? favourite = null, bool? removed = null);

    /// <summary>Adds setups from a file to the library; ones it already has (removed ones come back) aren't added twice.</summary>
    Task<SetupImportResult> ImportSetupsAsync(IReadOnlyList<NewSetup> setups, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PositionPoint>> GetPositionHistoryAsync(long recordingId, int maxPoints = 500, CancellationToken cancellationToken = default);
}
