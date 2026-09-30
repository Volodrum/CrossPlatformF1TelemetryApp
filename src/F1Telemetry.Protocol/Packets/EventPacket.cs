using System.Buffers.Binary;
using System.Text;

namespace F1Telemetry.Protocol.Packets;

/// <summary>
/// Event packet. The 4-character code is always decoded; of the per-event payload union only the button
/// bit field of <c>BUTN</c> is. Useful codes: SSTA (session started), SEND (session ended), FTLP (fastest lap),
/// FLBK (flashback), LGOT (lights out), CHQF (chequered flag), BUTN (button status changed).
/// </summary>
/// <param name="ButtonStatus">For <c>BUTN</c>: bit flags of the buttons held down right now (see <see cref="UdpActionMask"/>).</param>
public sealed record EventPacket(PacketHeader Header, string Code, uint? ButtonStatus = null) : Packet(Header)
{
    public const string SessionStarted = "SSTA";
    public const string SessionEnded = "SEND";
    public const string Flashback = "FLBK";
    public const string FastestLap = "FTLP";
    public const string Buttons = "BUTN";

    public const int UdpActionCount = 12;

    /// <summary>
    /// Bit of "UDP Action <paramref name="action"/>" (1–12) in <see cref="ButtonStatus"/>: 0x00100000 … 0x80000000.
    /// Players bind these to any wheel or pad button in the game's control settings; the game does nothing else
    /// with them, so they are safe to use for the app.
    /// </summary>
    public static uint UdpActionMask(int action) =>
        action is >= 1 and <= UdpActionCount ? 1u << (19 + action) : 0;

    internal static EventPacket Read(ReadOnlySpan<byte> buffer, PacketHeader header)
    {
        var code = Encoding.ASCII.GetString(buffer.Slice(FormatLayout.HeaderSize, 4));
        uint? buttons = code == Buttons ? BinaryPrimitives.ReadUInt32LittleEndian(buffer[(FormatLayout.HeaderSize + 4)..]) : null;
        return new EventPacket(header, code, buttons);
    }
}
