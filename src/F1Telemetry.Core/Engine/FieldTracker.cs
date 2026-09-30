using F1Telemetry.Core.Models;
using F1Telemetry.Protocol.Lookups;
using F1Telemetry.Protocol.Packets;

namespace F1Telemetry.Core.Engine;

/// <summary>
/// Latest state of every car in the session (not just the player): names and teams, lap data, tyres and battery,
/// and each car's best lap and best sectors from its session history. Feeds the timing tower and gives the
/// sector box its session-best and personal-best references.
/// </summary>
public sealed class FieldTracker
{
    /// <summary>ERS store capacity: <c>ersStoreEnergy</c> is reported in joules against a 4 MJ battery.</summary>
    public const double ErsCapacityJoules = 4_000_000;

    private const int MaxCars = 24;

    private readonly Participant?[] _participants = new Participant?[MaxCars];
    private readonly string[] _codes = new string[MaxCars];
    private readonly LapData?[] _laps = new LapData?[MaxCars];
    private readonly CarStatus?[] _status = new CarStatus?[MaxCars];
    private readonly CarBests?[] _bests = new CarBests?[MaxCars];
    private int _playerIndex;
    private int _sessionType;
    private int _totalLaps;
    private int _timeLeft;
    private float _trackLength;

    private sealed record CarBests(uint LapMs, uint LapS1, uint LapS2, uint S1, uint S2, uint S3);

    public float TrackLength => _trackLength;
    public float Sector2Start { get; private set; }
    public float Sector3Start { get; private set; }

    public void Reset()
    {
        Array.Clear(_participants);
        Array.Clear(_codes);
        Array.Clear(_laps);
        Array.Clear(_status);
        Array.Clear(_bests);
        _playerIndex = 0;
        _sessionType = 0;
        _totalLaps = 0;
        _timeLeft = 0;
        _trackLength = 0;
        Sector2Start = 0;
        Sector3Start = 0;
    }

    public void OnSession(SessionData data)
    {
        _sessionType = data.SessionType;
        _totalLaps = data.TotalLaps;
        _timeLeft = data.SessionTimeLeft;
        _trackLength = data.TrackLength;
        Sector2Start = data.Sector2LapDistanceStart;
        Sector3Start = data.Sector3LapDistanceStart;
    }

    public void OnParticipants(ParticipantsPacket packet)
    {
        for (var i = 0; i < Math.Min(packet.Cars.Length, MaxCars); i++)
        {
            _participants[i] = packet.Cars[i];
            _codes[i] = DriverCode(packet.Cars[i].Name);
        }
    }

    public void OnLapData(LapDataPacket packet)
    {
        _playerIndex = packet.Header.PlayerCarIndex;
        for (var i = 0; i < Math.Min(packet.Cars.Length, MaxCars); i++)
        {
            _laps[i] = packet.Cars[i];
        }
    }

    public void OnStatus(CarStatusPacket packet)
    {
        for (var i = 0; i < Math.Min(packet.Cars.Length, MaxCars); i++)
        {
            _status[i] = packet.Cars[i];
        }
    }

    public void OnHistory(SessionHistoryPacket packet)
    {
        if (packet.CarIndex >= MaxCars)
        {
            return;
        }

        var best = Lap(packet, packet.BestLapNum);
        _bests[packet.CarIndex] = new CarBests(
            best?.LapTimeMs ?? 0,
            best?.Sector1Ms ?? 0,
            best?.Sector2Ms ?? 0,
            Lap(packet, packet.BestSector1LapNum)?.Sector1Ms ?? 0,
            Lap(packet, packet.BestSector2LapNum)?.Sector2Ms ?? 0,
            Lap(packet, packet.BestSector3LapNum)?.Sector3Ms ?? 0);
    }

    /// <summary>Fastest lap of anyone this session, with its holder.</summary>
    public ReferenceLap? SessionBestLap()
    {
        ReferenceLap? best = null;
        for (var i = 0; i < MaxCars; i++)
        {
            if (_bests[i] is { LapMs: > 0 } b && (best is null || b.LapMs < best.LapMs))
            {
                best = new ReferenceLap(i, i == _playerIndex ? "YOU" : _codes[i] ?? "", b.LapMs, b.LapS1, b.LapS2);
            }
        }

        return best;
    }

    public ReferenceLap? PersonalBestLap() =>
        _bests[_playerIndex] is { LapMs: > 0 } b ? new ReferenceLap(_playerIndex, "YOU", b.LapMs, b.LapS1, b.LapS2) : null;

    /// <summary>Fastest time anyone has set in sector 1–3 this session (0 = none).</summary>
    public uint SessionBestSector(int sector)
    {
        uint best = 0;
        foreach (var b in _bests)
        {
            var ms = b is null ? 0 : SectorOf(b, sector);
            if (ms > 0 && (best == 0 || ms < best))
            {
                best = ms;
            }
        }

        return best;
    }

    public uint PersonalBestSector(int sector) => _bests[_playerIndex] is { } b ? SectorOf(b, sector) : 0;

    public FieldSnapshot Snapshot()
    {
        var cars = new List<CarTiming>(MaxCars);
        for (var i = 0; i < MaxCars; i++)
        {
            if (_laps[i] is not { } lap || lap.CarPosition == 0 || lap.ResultStatus < ResultStatus.Active)
            {
                continue;
            }

            var participant = _participants[i];
            var status = _status[i];
            var isPlayer = i == _playerIndex;
            var isAi = participant?.AiControlled ?? !isPlayer;
            var restricted = !isPlayer && participant is { AiControlled: false, TelemetryPublic: false };
            cars.Add(new CarTiming
            {
                CarIndex = i,
                Position = lap.CarPosition,
                Name = participant?.Name ?? "",
                Code = isPlayer ? "YOU" : _codes[i] is { Length: > 0 } code ? code : $"#{participant?.RaceNumber ?? i + 1}",
                TeamColour = participant is { } p ? TeamColour(p) : null,
                IsPlayer = isPlayer,
                IsAi = isAi,
                TelemetryRestricted = restricted,
                DeltaToCarInFrontMs = lap.DeltaToCarInFrontMs,
                DeltaToLeaderMs = lap.DeltaToRaceLeaderMs,
                TotalDistance = lap.TotalDistance,
                CurrentLap = lap.CurrentLapNum,
                PitStatus = lap.PitStatus,
                ResultStatus = lap.ResultStatus,
                DriverStatus = lap.DriverStatus,
                Compound = status is { } s ? Compounds.Visual(s.VisualTyreCompound) : VisualCompound.Unknown,
                TyreAgeLaps = status?.TyresAgeLaps ?? 0,
                ErsPercent = status is { } st && !restricted ? Math.Clamp(st.ErsStoreEnergy / ErsCapacityJoules * 100, 0, 100) : null,
                PenaltySeconds = lap.Penalties,
                TrackLimitWarnings = lap.CornerCuttingWarnings,
                BestLapMs = _bests[i]?.LapMs ?? 0,
            });
        }

        cars.Sort((a, b) => a.Position.CompareTo(b.Position));
        return new FieldSnapshot(cars, _playerIndex, _sessionType, _totalLaps, _timeLeft, _trackLength);
    }

    /// <summary>"Max VERSTAPPEN" / "VERSTAPPEN" → "VER"; gamertags keep their first three letters.</summary>
    public static string DriverCode(string? name)
    {
        name ??= "";
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var source = parts.Length == 0 ? "" : parts[^1];
        var letters = new string(source.Where(char.IsLetter).ToArray());
        if (letters.Length < 3)
        {
            letters = new string(name.Where(char.IsLetterOrDigit).ToArray());
        }

        return letters.Length <= 3 ? letters.ToUpperInvariant() : letters[..3].ToUpperInvariant();
    }

    private static uint? TeamColour(Participant p)
    {
        if (Teams.Colour(p.TeamId) is { } known)
        {
            return known;
        }

        return p.LiveryColours is [var c, ..] ? (uint)(c.Red << 16 | c.Green << 8 | c.Blue) : null;
    }

    private static LapHistory? Lap(SessionHistoryPacket packet, byte lapNum) =>
        lapNum >= 1 && lapNum <= packet.Laps.Length ? packet.Laps[lapNum - 1] : null;

    private static uint SectorOf(CarBests b, int sector) => sector switch
    {
        1 => b.S1,
        2 => b.S2,
        _ => b.S3,
    };
}
