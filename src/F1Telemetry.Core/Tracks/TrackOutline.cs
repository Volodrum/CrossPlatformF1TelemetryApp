using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace F1Telemetry.Core.Tracks;

public readonly record struct TrackPoint(double X, double Z);

/// <summary>
/// Left/right track limits in game world coordinates (X, Z), so recorded <c>world_pos_x/z</c> values can be
/// drawn directly on top.
/// </summary>
public sealed class TrackOutline
{
    public TrackOutline(string name, IReadOnlyList<TrackPoint> left, IReadOnlyList<TrackPoint> right)
    {
        Name = name;
        Left = left;
        Right = right;
        var count = Math.Min(left.Count, right.Count);
        Centerline = Enumerable.Range(0, count)
            .Select(i => new TrackPoint((left[i].X + right[i].X) / 2, (left[i].Z + right[i].Z) / 2))
            .ToList();

        var all = left.Concat(right).ToList();
        MinX = all.Min(p => p.X);
        MaxX = all.Max(p => p.X);
        MinZ = all.Min(p => p.Z);
        MaxZ = all.Max(p => p.Z);
    }

    public string Name { get; }
    public IReadOnlyList<TrackPoint> Left { get; }
    public IReadOnlyList<TrackPoint> Right { get; }
    public IReadOnlyList<TrackPoint> Centerline { get; }
    public double MinX { get; }
    public double MaxX { get; }
    public double MinZ { get; }
    public double MaxZ { get; }
}

/// <summary>
/// Reader for Sim Racing Telemetry <c>.srtt</c> track files. Layout (reverse engineered): a <c>TRKD</c> chunk
/// holding <c>pointCount</c> 24-byte records; the first half is the left boundary, the second half the right
/// boundary shifted by 4 bytes and led by one dummy record. Each record starts with float X, Z, Y.
/// SRTT X is mirrored relative to game world X.
/// </summary>
public static class SrttReader
{
    private const int RecordSize = 24;

    public static TrackOutline Read(string path) => Read(File.ReadAllBytes(path), Path.GetFileNameWithoutExtension(path));

    public static TrackOutline Read(ReadOnlySpan<byte> data, string name)
    {
        var chunk = data.IndexOf("TRKD"u8);
        if (chunk < 0)
        {
            throw new InvalidDataException($"TRKD section not found in track '{name}'.");
        }

        var pointCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[(chunk + 8)..]);
        var dataOffset = chunk + 12;
        var half = pointCount / 2;

        var left = new List<TrackPoint>(half);
        for (var i = 0; i < half; i++)
        {
            var offset = dataOffset + i * RecordSize;
            if (offset + RecordSize > data.Length)
            {
                break;
            }

            left.Add(ReadPoint(data, offset));
        }

        var right = new List<TrackPoint>(pointCount - half);
        for (var i = 1; i < pointCount - half; i++)
        {
            var offset = dataOffset + (half + i) * RecordSize + 4;
            if (offset + RecordSize > data.Length)
            {
                break;
            }

            right.Add(ReadPoint(data, offset));
        }

        return new TrackOutline(name, left, right);
    }

    private static TrackPoint ReadPoint(ReadOnlySpan<byte> data, int offset)
    {
        var x = BinaryPrimitives.ReadSingleLittleEndian(data[offset..]);
        var z = BinaryPrimitives.ReadSingleLittleEndian(data[(offset + 4)..]);
        return new TrackPoint(-x, z);
    }
}

/// <summary>
/// Reader for the curated track maps (<c>track_maps/&lt;name&gt;_coords.json</c>, the same files the original
/// dashboard used): <c>{ "trackName", "left": [{x,z,y}], "right": [{x,z,y}] }</c> in SRTT orientation, so X is
/// mirrored into game world space here.
/// </summary>
public static class TrackMapJsonReader
{
    public static TrackOutline Read(string path)
    {
        using var stream = File.OpenRead(path);
        var map = JsonSerializer.Deserialize<MapFile>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                  ?? throw new InvalidDataException($"Empty track map {path}");
        return new TrackOutline(
            map.TrackName ?? Path.GetFileNameWithoutExtension(path),
            map.Left.Select(p => new TrackPoint(-p.X, p.Z)).ToList(),
            map.Right.Select(p => new TrackPoint(-p.X, p.Z)).ToList());
    }

    /// <summary>"Silverstone (reverse)" → "silverstone_reverse" (the file naming used by the maps folder).</summary>
    public static string FileKey(string mapName) =>
        new string(mapName.ToLowerInvariant().Replace(' ', '_').Where(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_').ToArray());

    private sealed record MapFile(string? TrackName, List<MapPoint> Left, List<MapPoint> Right);

    private sealed record MapPoint(double X, double Z, double Y);
}

/// <summary>
/// Resolves and caches track outlines: the curated JSON maps first (<paramref name="mapsDirectory"/>), then raw
/// <c>.srtt</c> files as a fallback (<paramref name="srttDirectory"/>).
/// </summary>
public sealed class TrackLibrary(string mapsDirectory, string? srttDirectory = null)
{
    private readonly Dictionary<string, TrackOutline?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    public string MapsDirectory => mapsDirectory;

    public TrackOutline? Get(string? mapName)
    {
        if (string.IsNullOrEmpty(mapName))
        {
            return null;
        }

        lock (_gate)
        {
            if (_cache.TryGetValue(mapName, out var cached))
            {
                return cached;
            }

            var json = Path.Combine(mapsDirectory, TrackMapJsonReader.FileKey(mapName) + "_coords.json");
            var srtt = srttDirectory is null ? null : Path.Combine(srttDirectory, mapName + ".srtt");
            var outline = File.Exists(json) ? TrackMapJsonReader.Read(json)
                : srtt is not null && File.Exists(srtt) ? SrttReader.Read(srtt)
                : null;
            _cache[mapName] = outline;
            return outline;
        }
    }

    public TrackOutline? ForTrackId(int trackId) =>
        Get(Protocol.Lookups.Tracks.Get((sbyte)trackId).MapFile);
}
