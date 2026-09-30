using System.Text;

namespace F1Telemetry.Protocol.Packets;

public readonly record struct LiveryColour(byte Red, byte Green, byte Blue);

/// <param name="Name">Driver name for AI cars; for online players the gamertag (or a placeholder when names are hidden).</param>
/// <param name="TelemetryPublic">False when a human player restricts telemetry: their status and damage data are zeroed.</param>
public readonly record struct Participant(
    bool AiControlled,
    ushort DriverId,
    ushort NetworkId,
    ushort TeamId,
    bool MyTeam,
    byte RaceNumber,
    byte Nationality,
    string Name,
    bool TelemetryPublic,
    bool ShowOnlineNames,
    ushort TechLevel,
    byte Platform,
    LiveryColour[] LiveryColours)
{
    private const int NameBytes = 32;
    private const int MaxLiveryColours = 4;

    /// <summary>2026 widens driverId, networkId and teamId from u8 to u16 (57 → 60 bytes per car).</summary>
    internal static Participant Read(ref SpanReader r, ReadOnlySpan<byte> buffer, FormatLayout layout)
    {
        var wide = layout.WideParticipantIds;
        var ai = r.Bool();
        var driverId = wide ? r.U16() : r.U8();
        var networkId = wide ? r.U16() : r.U8();
        var teamId = wide ? r.U16() : r.U8();
        var myTeam = r.Bool();
        var raceNumber = r.U8();
        var nationality = r.U8();
        var name = DecodeName(buffer.Slice(r.Position, NameBytes));
        r.Skip(NameBytes);
        var telemetryPublic = r.Bool();
        var showNames = r.Bool();
        var techLevel = r.U16();
        var platform = r.U8();
        var numColours = Math.Min((int)r.U8(), MaxLiveryColours);
        var colours = new LiveryColour[numColours];
        var coloursStart = r.Position;
        for (var i = 0; i < numColours; i++)
        {
            colours[i] = new LiveryColour(r.U8(), r.U8(), r.U8());
        }

        r.Position = coloursStart + MaxLiveryColours * 3;
        return new Participant(ai, driverId, networkId, teamId, myTeam, raceNumber, nationality, name, telemetryPublic, showNames,
            techLevel, platform, colours);
    }

    private static string DecodeName(ReadOnlySpan<byte> bytes)
    {
        var end = bytes.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end >= 0 ? bytes[..end] : bytes).Trim();
    }
}

/// <summary>Names, teams and telemetry settings of every car. Sent every 5 seconds.</summary>
public sealed record ParticipantsPacket(PacketHeader Header, byte NumActiveCars, Participant[] Cars) : Packet(Header)
{
    internal static ParticipantsPacket Read(ref SpanReader r, ReadOnlySpan<byte> buffer, PacketHeader header, FormatLayout layout)
    {
        var numActive = r.U8();
        var cars = new Participant[layout.MaxCars];
        for (var i = 0; i < cars.Length; i++)
        {
            var start = r.Position;
            cars[i] = Participant.Read(ref r, buffer, layout);
            r.Position = start + layout.ParticipantSlot;
        }

        return new ParticipantsPacket(header, numActive, cars);
    }
}
