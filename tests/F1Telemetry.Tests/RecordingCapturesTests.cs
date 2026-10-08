using F1Telemetry.Core.Models;
using F1Telemetry.Ingest;
using F1Telemetry.Protocol;

namespace F1Telemetry.Tests;

public sealed class RecordingCapturesTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("captures-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static RecordingInfo Recording(long id, DateTime localStart) =>
        new(id, "uid", "", 11, "Monza", 10, GameFormat.F1_26, new DateTimeOffset(localStart).ToUniversalTime(), null);

    private string Capture(long id, DateTime localStart)
    {
        var path = Path.Combine(_directory, RecordingCaptures.FileName(id, localStart));
        File.WriteAllText(path, "");
        return path;
    }

    [Fact]
    public void Names_carry_the_recording_and_its_local_start()
    {
        var start = new DateTime(2026, 10, 6, 16, 20, 9);

        Assert.Equal("recording-17-20261006-162009.f1rec", RecordingCaptures.FileName(17, start));
        Assert.Equal((17L, start), RecordingCaptures.Parse(@"C:\data\captures\recording-17-20261006-162009.f1rec"));
        Assert.Equal((17L, start), RecordingCaptures.Parse("/home/me/.local/share/F1Telemetry/captures/recording-17-20261006-162009.f1rec"));
        Assert.Null(RecordingCaptures.Parse("capture-20261006-162009.f1rec"));
        Assert.Null(RecordingCaptures.Parse("recording-17-20261006-162009.txt"));
    }

    [Fact]
    public void A_recording_owns_the_capture_that_started_with_it()
    {
        var start = new DateTime(2026, 10, 6, 16, 47, 15);
        var own = Capture(18, start.AddSeconds(1));
        Capture(18, start.AddHours(-2));            // a leftover of a deleted recording whose id was reused
        Capture(19, start);                         // another recording that still exists, started the same second
        Capture(20, start.AddMinutes(30));          // the next session

        Assert.Equal([own], RecordingCaptures.Find(_directory, Recording(18, start), new HashSet<long> { 18, 19, 20 }));
    }

    [Fact]
    public void A_reimported_recording_owns_the_capture_it_was_rebuilt_from()
    {
        // #20 was rebuilt into #21, which kept #20's start time; #20 is gone.
        var start = new DateTime(2026, 10, 6, 17, 14, 24);
        var source = Capture(20, start);

        Assert.Equal([source], RecordingCaptures.Find(_directory, Recording(21, start), new HashSet<long> { 21 }));
        Assert.Empty(RecordingCaptures.Find(Path.Combine(_directory, "missing"), Recording(21, start), new HashSet<long>()));
    }
}
