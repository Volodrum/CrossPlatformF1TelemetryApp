using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace F1Telemetry.Ingest;

/// <summary>
/// <c>.f1rec</c> raw capture format: <c>"F1REC" 0x01</c> magic, then records of
/// <c>int64 timestampTicks | int32 length | byte[length]</c> (little-endian). Raw datagrams are kept verbatim so
/// captures stay replayable even after parser changes.
/// </summary>
public static class PacketFile
{
    public static ReadOnlySpan<byte> Magic => "F1REC\x01"u8;
    public const string Extension = ".f1rec";
}

public sealed class PacketFileWriter : IAsyncDisposable, IDisposable
{
    private readonly FileStream _stream;
    private readonly Lock _gate = new();
    private readonly byte[] _recordHeader = new byte[12];

    private PacketFileWriter(FileStream stream) => _stream = stream;

    public string Path => _stream.Name;
    public long PacketCount { get; private set; }

    public static PacketFileWriter Create(string path)
    {
        var directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
        stream.Write(PacketFile.Magic);
        return new PacketFileWriter(stream);
    }

    public void Write(in RawPacket packet)
    {
        lock (_gate)
        {
            BinaryPrimitives.WriteInt64LittleEndian(_recordHeader, packet.Timestamp.Ticks);
            BinaryPrimitives.WriteInt32LittleEndian(_recordHeader.AsSpan(8), packet.Data.Length);
            _stream.Write(_recordHeader);
            _stream.Write(packet.Data);
            PacketCount++;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _stream.Dispose();
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed class PacketFileReader : IAsyncDisposable
{
    private readonly FileStream _stream;

    private PacketFileReader(FileStream stream) => _stream = stream;

    public static PacketFileReader Open(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, useAsync: true);
        Span<byte> magic = stackalloc byte[PacketFile.Magic.Length];
        if (stream.Read(magic) != magic.Length || !magic.SequenceEqual(PacketFile.Magic))
        {
            stream.Dispose();
            throw new InvalidDataException($"'{path}' is not an .f1rec capture.");
        }

        return new PacketFileReader(stream);
    }

    public async IAsyncEnumerable<RawPacket> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var header = new byte[12];
        while (true)
        {
            if (await _stream.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken) < header.Length)
            {
                yield break;
            }

            var ticks = BinaryPrimitives.ReadInt64LittleEndian(header);
            var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8));
            if (length is <= 0 or > 65_535)
            {
                throw new InvalidDataException($"Corrupt record length {length} at offset {_stream.Position - 12}.");
            }

            var data = new byte[length];
            if (await _stream.ReadAtLeastAsync(data, length, throwOnEndOfStream: false, cancellationToken) < length)
            {
                yield break; // truncated tail (capture interrupted)
            }

            yield return new RawPacket(TimeSpan.FromTicks(ticks), data);
        }
    }

    public ValueTask DisposeAsync() => _stream.DisposeAsync();
}
