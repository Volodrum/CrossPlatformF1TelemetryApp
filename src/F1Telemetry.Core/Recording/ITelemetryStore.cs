using F1Telemetry.Core.Models;

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

    Task<IReadOnlyList<LapAggregate>> GetLapAggregatesAsync(long recordingId, CancellationToken cancellationToken = default);

    Task<WheelValues> GetLatestWearAsync(long recordingId, CancellationToken cancellationToken = default);

    /// <summary>The setups driven in a recording, in the order they arrived.</summary>
    Task<IReadOnlyList<SetupChange>> GetSetupsAsync(long recordingId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PositionPoint>> GetPositionHistoryAsync(long recordingId, int maxPoints = 500, CancellationToken cancellationToken = default);
}
