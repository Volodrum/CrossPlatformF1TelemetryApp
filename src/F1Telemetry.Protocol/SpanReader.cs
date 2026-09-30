using System.Buffers.Binary;

namespace F1Telemetry.Protocol;

/// <summary>
/// Sequential little-endian reader over a packet buffer. All F1 UDP structs are packed with no padding,
/// so reading fields in declaration order mirrors the official specification one-to-one.
/// </summary>
public ref struct SpanReader
{
    private readonly ReadOnlySpan<byte> _buffer;

    public SpanReader(ReadOnlySpan<byte> buffer, int position = 0)
    {
        _buffer = buffer;
        Position = position;
    }

    public int Position { get; set; }

    public byte U8() => _buffer[Position++];

    public sbyte I8() => unchecked((sbyte)_buffer[Position++]);

    public bool Bool() => _buffer[Position++] != 0;

    public ushort U16()
    {
        var value = BinaryPrimitives.ReadUInt16LittleEndian(_buffer[Position..]);
        Position += 2;
        return value;
    }

    public short I16()
    {
        var value = BinaryPrimitives.ReadInt16LittleEndian(_buffer[Position..]);
        Position += 2;
        return value;
    }

    public uint U32()
    {
        var value = BinaryPrimitives.ReadUInt32LittleEndian(_buffer[Position..]);
        Position += 4;
        return value;
    }

    public ulong U64()
    {
        var value = BinaryPrimitives.ReadUInt64LittleEndian(_buffer[Position..]);
        Position += 8;
        return value;
    }

    public float F32()
    {
        var value = BinaryPrimitives.ReadSingleLittleEndian(_buffer[Position..]);
        Position += 4;
        return value;
    }

    /// <summary>Signed 16-bit value normalised to [-1, 1] (direction vectors).</summary>
    public float Normalised16() => I16() / 32767f;

    /// <summary>Time split into a millisecond part (u16) and a minutes part (u8).</summary>
    public uint SplitTimeMs()
    {
        uint ms = U16();
        uint minutes = U8();
        return ms + minutes * 60_000u;
    }

    public Tyres<byte> TyresU8() => new(U8(), U8(), U8(), U8());

    public Tyres<ushort> TyresU16() => new(U16(), U16(), U16(), U16());

    public Tyres<float> TyresF32() => new(F32(), F32(), F32(), F32());

    public void Skip(int bytes) => Position += bytes;
}
