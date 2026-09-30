namespace F1Telemetry.Protocol.Packets;

public readonly record struct WeatherForecastSample(
    byte SessionType,
    byte TimeOffsetMinutes,
    byte Weather,
    sbyte TrackTemperature,
    sbyte TrackTemperatureChange,
    sbyte AirTemperature,
    sbyte AirTemperatureChange,
    byte RainPercentage);

public readonly record struct TrackZone(float Start, float End);

/// <summary>2026-only additions to the session packet.</summary>
public sealed record SessionAeroInfo(
    byte ActiveAeroTrackStatus,
    TrackZone[] ActiveAeroZonesFull,
    TrackZone[] ActiveAeroZonesPartial,
    TrackZone[] DrsZones,
    float StartReactionTime);

public sealed record SessionData(
    byte Weather,
    sbyte TrackTemperature,
    sbyte AirTemperature,
    byte TotalLaps,
    ushort TrackLength,
    byte SessionType,
    sbyte TrackId,
    byte Formula,
    ushort SessionTimeLeft,
    ushort SessionDuration,
    byte PitSpeedLimit,
    bool GamePaused,
    byte SafetyCarStatus,
    bool IsNetworkGame,
    WeatherForecastSample[] WeatherForecast,
    byte ForecastAccuracy,
    byte AiDifficulty,
    byte PitStopWindowIdealLap,
    byte PitStopWindowLatestLap,
    byte PitStopRejoinPosition,
    byte NumSafetyCarPeriods,
    byte NumVirtualSafetyCarPeriods,
    byte NumRedFlagPeriods,
    float Sector2LapDistanceStart,
    float Sector3LapDistanceStart,
    SessionAeroInfo? Aero);

public sealed record SessionPacket(PacketHeader Header, SessionData Data) : Packet(Header)
{
    private const int MarshalZoneCount = 21;
    private const int MarshalZoneSize = 5;
    private const int ForecastSampleCount = 64;

    internal static SessionPacket Read(ref SpanReader r, PacketHeader header, FormatLayout layout)
    {
        var weather = r.U8();
        var trackTemp = r.I8();
        var airTemp = r.I8();
        var totalLaps = r.U8();
        var trackLength = r.U16();
        var sessionType = r.U8();
        var trackId = r.I8();
        var formula = r.U8();
        var timeLeft = r.U16();
        var duration = r.U16();
        var pitSpeedLimit = r.U8();
        var paused = r.Bool();
        r.Skip(3); // isSpectating, spectatorCarIndex, sliProNativeSupport
        r.Skip(1 + MarshalZoneCount * MarshalZoneSize); // numMarshalZones + marshal zones
        var safetyCar = r.U8();
        var network = r.Bool();
        var numForecast = Math.Min(r.U8(), (byte)ForecastSampleCount);

        var forecast = new WeatherForecastSample[numForecast];
        var forecastStart = r.Position;
        for (var i = 0; i < numForecast; i++)
        {
            forecast[i] = new WeatherForecastSample(r.U8(), r.U8(), r.U8(), r.I8(), r.I8(), r.I8(), r.I8(), r.U8());
        }

        r.Position = forecastStart + ForecastSampleCount * 8;
        var forecastAccuracy = r.U8();
        var aiDifficulty = r.U8();
        r.Skip(12); // season / weekend / session link identifiers
        var pitIdeal = r.U8();
        var pitLatest = r.U8();
        var pitRejoin = r.U8();
        r.Skip(11); // 7 assists + racing line (2) + game mode + rule set
        r.Skip(4);  // time of day
        r.Skip(5);  // session length + unit settings
        var numSc = r.U8();
        var numVsc = r.U8();
        var numRed = r.U8();
        r.Skip(24); // equal performance .. affects licence level MP
        r.Skip(1 + 12); // numSessionsInWeekend + weekendStructure
        var s2Start = r.F32();
        var s3Start = r.F32();

        SessionAeroInfo? aero = null;
        if (layout.HasSessionAeroBlock)
        {
            var status = r.U8();
            var full = ReadZones(ref r, 8);
            var partial = ReadZones(ref r, 8);
            var drs = ReadZones(ref r, 4);
            var reaction = r.F32();
            aero = new SessionAeroInfo(status, full, partial, drs, reaction);
        }

        return new SessionPacket(header, new SessionData(
            weather, trackTemp, airTemp, totalLaps, trackLength, sessionType, trackId, formula, timeLeft, duration,
            pitSpeedLimit, paused, safetyCar, network, forecast, forecastAccuracy, aiDifficulty, pitIdeal, pitLatest,
            pitRejoin, numSc, numVsc, numRed, s2Start, s3Start, aero));
    }

    private static TrackZone[] ReadZones(ref SpanReader r, int capacity)
    {
        var count = Math.Min((int)r.U8(), capacity);
        var start = r.Position;
        var zones = new TrackZone[count];
        for (var i = 0; i < count; i++)
        {
            zones[i] = new TrackZone(r.F32(), r.F32());
        }

        r.Position = start + capacity * 8;
        return zones;
    }
}
