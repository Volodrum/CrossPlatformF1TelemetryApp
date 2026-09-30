using F1Telemetry.Core.Models;
using F1Telemetry.Protocol.Packets;

namespace F1Telemetry.Core.Engine;

/// <summary>
/// Drives the qualifying-style sector box from the player's lap data. A sector's colour and its gap to the
/// reference laps are fixed at the moment the split is crossed (later history updates, which may already include
/// that very lap, must not change them). A finished sector is reported (<see cref="SectorBoxSnapshot.LastSplit"/>) for
/// <see cref="SplitSeconds"/> after the split; after the line the finished lap is held for <see cref="HoldSeconds"/>.
/// </summary>
public sealed class SectorTimer
{
    public const double SplitSeconds = 4;
    public const double HoldSeconds = 4;

    private readonly SectorMark[] _marks = new SectorMark[3];
    private readonly uint[] _sectorMs = new uint[3];
    private readonly SectorSplit?[] _splits = new SectorSplit?[2];
    private readonly double[] _splitTimes = new double[2];
    private int _lapNum;
    private bool _invalid;
    private bool _outOrIn;
    private SectorBoxSnapshot? _hold;
    private double _holdUntil;

    public void Reset()
    {
        _lapNum = 0;
        _hold = null;
        ClearLap();
    }

    public SectorBoxSnapshot Update(LapData lap, double sessionTime, FieldTracker field)
    {
        var lapNum = (int)lap.CurrentLapNum;
        var sessionBest = field.SessionBestLap();
        var personalBest = field.PersonalBestLap();

        if (_lapNum > 0 && lapNum == _lapNum + 1)
        {
            if (FinishLap(lap, sessionBest, personalBest, field) is { } finished)
            {
                _hold = finished;
                _holdUntil = sessionTime + HoldSeconds;
            }

            ClearLap();
        }
        else if (lapNum != _lapNum)
        {
            // Flashback to an earlier lap, a skipped lap or the first packet: start clean.
            _hold = null;
            ClearLap();
        }

        _lapNum = lapNum;
        _invalid = lap.CurrentLapInvalid;
        _outOrIn = lap.DriverStatus is DriverStatus.OutLap or DriverStatus.InLap or DriverStatus.InGarage || lap.PitStatus != PitStatus.None;
        TrackSplits(lap, sessionTime, sessionBest, personalBest, field);

        if (_hold is not null && sessionTime < _holdUntil && sessionTime >= _holdUntil - HoldSeconds)
        {
            return _hold with { Position = lap.CarPosition };
        }

        _hold = null;
        return Live(lap, sessionTime, sessionBest, personalBest, field);
    }

    private void ClearLap()
    {
        Array.Clear(_marks);
        Array.Clear(_sectorMs);
        Array.Clear(_splits);
        _invalid = false;
    }

    /// <summary>The latest split of this lap while it is still on show (<see cref="SplitSeconds"/>).</summary>
    private SectorSplit? VisibleSplit(double sessionTime)
    {
        var i = _splits[1] is not null ? 1 : _splits[0] is not null ? 0 : -1;
        return i >= 0 && sessionTime - _splitTimes[i] < SplitSeconds ? _splits[i] : null;
    }

    private void TrackSplits(LapData lap, double sessionTime, ReferenceLap? sessionBest, ReferenceLap? personalBest, FieldTracker field)
    {
        // A flashback within the lap moves the car back into an earlier sector: forget the splits after it.
        for (var s = lap.Sector; s < 2; s++)
        {
            if (_sectorMs[s] != 0)
            {
                _sectorMs[s] = 0;
                _marks[s] = SectorMark.Pending;
                _splits[s] = null;
            }
        }

        if (lap.Sector >= 1 && _sectorMs[0] == 0 && lap.Sector1TimeMs > 0)
        {
            Split(1, lap.Sector1TimeMs, sessionTime, sessionBest, personalBest, field);
        }

        if (lap.Sector >= 2 && _sectorMs[1] == 0 && lap.Sector2TimeMs > 0 && _sectorMs[0] > 0)
        {
            Split(2, lap.Sector2TimeMs, sessionTime, sessionBest, personalBest, field);
        }
    }

    private void Split(int sector, uint ms, double sessionTime, ReferenceLap? sessionBest, ReferenceLap? personalBest, FieldTracker field)
    {
        _sectorMs[sector - 1] = ms;
        _splitTimes[sector - 1] = sessionTime;
        _marks[sector - 1] = Mark(sector, ms, field);
        var cumulative = _sectorMs.Take(sector).Aggregate(0u, (a, b) => a + b);
        _splits[sector - 1] = new SectorSplit(sector, ms, Gap(cumulative, sessionBest, sector), Gap(cumulative, personalBest, sector));
    }

    private SectorBoxSnapshot? FinishLap(LapData lap, ReferenceLap? sessionBest, ReferenceLap? personalBest, FieldTracker field)
    {
        var lapMs = lap.LastLapTimeMs;
        if (_outOrIn || lapMs == 0 || _sectorMs[0] == 0 || _sectorMs[1] == 0 || lapMs <= _sectorMs[0] + _sectorMs[1])
        {
            return null;
        }

        var s3 = lapMs - _sectorMs[0] - _sectorMs[1];
        var invalid = _invalid;
        var marks = invalid
            ? [SectorMark.Invalid, SectorMark.Invalid, SectorMark.Invalid]
            : new[] { _marks[0], _marks[1], Mark(3, s3, field) };

        return new SectorBoxSnapshot
        {
            Position = lap.CarPosition,
            LapTimeMs = lapMs,
            IsFinished = true,
            IsInvalid = invalid,
            Sectors = marks,
            LastSplit = invalid ? null : new SectorSplit(3, s3, Gap(lapMs, sessionBest, 3), Gap(lapMs, personalBest, 3)),
            LapIsSessionBest = !invalid && (sessionBest is null || lapMs < sessionBest.LapMs),
            LapIsPersonalBest = !invalid && (personalBest is null || lapMs < personalBest.LapMs),
            SessionBest = sessionBest,
            PersonalBest = personalBest,
        };
    }

    private SectorBoxSnapshot Live(LapData lap, double sessionTime, ReferenceLap? sessionBest, ReferenceLap? personalBest, FieldTracker field)
    {
        var marks = new SectorMark[3];
        if (!_outOrIn)
        {
            for (var i = 0; i < 3; i++)
            {
                marks[i] = i < lap.Sector ? (_invalid ? SectorMark.Invalid : _marks[i]) : i == lap.Sector ? SectorMark.Live : SectorMark.Pending;
            }
        }

        return new SectorBoxSnapshot
        {
            Position = lap.CarPosition,
            LapTimeMs = lap.CurrentLapTimeMs,
            IsInvalid = _invalid && !_outOrIn,
            IsOutOrInLap = _outOrIn,
            Sectors = marks,
            LiveProgress = _outOrIn ? 0 : Progress(lap, field),
            LastSplit = _outOrIn || _invalid ? null : VisibleSplit(sessionTime),
            SessionBest = sessionBest,
            PersonalBest = personalBest,
        };
    }

    /// <summary>Purple if no one has been quicker this session, green if it's your best, yellow otherwise.</summary>
    private static SectorMark Mark(int sector, uint ms, FieldTracker field)
    {
        var sessionBest = field.SessionBestSector(sector);
        if (sessionBest == 0 || ms <= sessionBest)
        {
            return SectorMark.SessionBest;
        }

        var personalBest = field.PersonalBestSector(sector);
        return personalBest == 0 || ms <= personalBest ? SectorMark.PersonalBest : SectorMark.Slower;
    }

    private static int? Gap(uint timeAtSplit, ReferenceLap? reference, int sector) =>
        reference is null || reference.SplitAt(sector) == 0 ? null : (int)timeAtSplit - (int)reference.SplitAt(sector);

    /// <summary>Distance through the current sector, using the session's sector boundaries (thirds when unknown).</summary>
    private static double Progress(LapData lap, FieldTracker field)
    {
        var length = field.TrackLength;
        if (length <= 0)
        {
            return 0;
        }

        var s2 = field.Sector2Start > 0 ? field.Sector2Start : length / 3;
        var s3 = field.Sector3Start > s2 ? field.Sector3Start : 2 * length / 3;
        var (start, end) = lap.Sector switch
        {
            0 => (0f, s2),
            1 => (s2, s3),
            _ => (s3, length),
        };

        return end > start ? Math.Clamp((lap.LapDistance - start) / (end - start), 0, 1) : 0;
    }
}
