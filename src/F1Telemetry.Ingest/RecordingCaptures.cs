using System.Globalization;
using F1Telemetry.Core.Models;

namespace F1Telemetry.Ingest;

/// <summary>
/// The raw captures the app saves next to its recordings: <c>recording-17-20261006-162009.f1rec</c>, the recording's id
/// and the local time it started.
/// </summary>
public static class RecordingCaptures
{
    private const string TimeFormat = "yyyyMMdd-HHmmss";

    /// <summary>A capture's start may trail the recording's by the time the recording event takes to arrive.</summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(10);

    /// <summary>The file name for the capture of recording <paramref name="recordingId"/> started at <paramref name="localStart"/>.</summary>
    public static string FileName(long recordingId, DateTime localStart) =>
        $"recording-{recordingId}-{localStart.ToString(TimeFormat, CultureInfo.InvariantCulture)}{PacketFile.Extension}";

    /// <summary>The recording id and local start time in a capture's file name, or null for any other file.</summary>
    public static (long RecordingId, DateTime LocalStart)? Parse(string path)
    {
        if (!path.EndsWith(PacketFile.Extension, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var parts = Path.GetFileNameWithoutExtension(path).Split('-');
        return parts is ["recording", var id, var date, var time]
               && long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var recordingId)
               && DateTime.TryParseExact($"{date}-{time}", TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
            ? (recordingId, start)
            : null;
    }

    /// <summary>
    /// The captures in <paramref name="directory"/> that hold <paramref name="recording"/>: started when it did, and
    /// named after it, or after a recording that no longer exists (a capture re-imported into a new recording keeps the
    /// original's start time but not its id). A capture named after another recording in <paramref name="existingIds"/>
    /// is that recording's, and a leftover whose id was reused started at another time; neither is returned.
    /// </summary>
    public static IReadOnlyList<string> Find(string directory, RecordingInfo recording, IReadOnlySet<long> existingIds)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var start = recording.StartTime.ToLocalTime().DateTime;
        return
        [
            .. Directory.EnumerateFiles(directory, "recording-*" + PacketFile.Extension)
                .Where(path => Parse(path) is { } capture
                               && (capture.RecordingId == recording.Id || !existingIds.Contains(capture.RecordingId))
                               && (capture.LocalStart - start).Duration() <= Tolerance),
        ];
    }
}
