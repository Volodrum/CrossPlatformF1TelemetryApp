using F1Telemetry.Core.Models;
using F1Telemetry.Protocol.Packets;

namespace F1Telemetry.Core.Engine;

public sealed record StrategyOptions
{
    /// <summary>Wear percentage treated as "tyre finished" for laps-remaining projections.</summary>
    public double TyreWearLimitPercent { get; init; } = 75;

    /// <summary>Fuel deltas outside (0, max) are treated as refuel / flashback artefacts.</summary>
    public double MaxPlausibleFuelPerLap { get; init; } = 10;
}

/// <summary>
/// Measures fuel and tyre consumption lap by lap for the live strategy overlay. Only clean laps (no pit,
/// no out/in-lap, no SC) contribute; averages are kept per compound, and per-tyre wear history resets
/// when a tyre change is detected.
/// </summary>
public sealed class ConsumptionTracker(StrategyOptions options)
{
    private readonly Dictionary<string, List<double>> _fuelPerLap = [];
    private readonly Dictionary<string, List<WheelValues>> _wearPerLap = [];
    private double? _fuelAtLapStart;
    private WheelValues? _wearAtLapStart;

    public StrategyOptions Options => options;

    public void Reset()
    {
        _fuelPerLap.Clear();
        _wearPerLap.Clear();
        _fuelAtLapStart = null;
        _wearAtLapStart = null;
    }

    /// <summary>Seeds the lap-start reference the first time status/damage data is available.</summary>
    public void EnsureStarted(CarStatus? status, CarDamage? damage)
    {
        if (_fuelAtLapStart is null && status is { } s)
        {
            _fuelAtLapStart = s.FuelInTank;
        }

        if (_wearAtLapStart is null && damage is { } d)
        {
            _wearAtLapStart = ToWheels(d);
        }
    }

    public void OnLapCompleted(string compound, bool countsTowardsAverages, CarStatus? status, CarDamage? damage)
    {
        var wearNow = damage is { } d ? ToWheels(d) : (WheelValues?)null;

        if (_wearAtLapStart is { } wearStart && wearNow is { } wearEnd && TyreChangeDetected(wearStart, wearEnd))
        {
            _wearPerLap.Remove(compound);
        }
        else if (countsTowardsAverages)
        {
            if (_fuelAtLapStart is { } fuelStart && status is { } s)
            {
                var used = fuelStart - s.FuelInTank;
                if (used > 0 && used < options.MaxPlausibleFuelPerLap)
                {
                    GetList(_fuelPerLap, compound).Add(used);
                }
            }

            if (_wearAtLapStart is { } ws && wearNow is { } we)
            {
                var delta = new WheelValues(we.Fl - ws.Fl, we.Fr - ws.Fr, we.Rl - ws.Rl, we.Rr - ws.Rr);
                if (delta is { Fl: >= 0, Fr: >= 0, Rl: >= 0, Rr: >= 0 })
                {
                    GetList(_wearPerLap, compound).Add(delta);
                }
            }
        }

        _fuelAtLapStart = status?.FuelInTank ?? _fuelAtLapStart;
        _wearAtLapStart = wearNow ?? _wearAtLapStart;
    }

    public double? AverageFuelPerLap(string compound) =>
        _fuelPerLap.TryGetValue(compound, out var list) && list.Count > 0 ? list.Average() : null;

    public WheelValues? AverageWearPerLap(string compound)
    {
        if (!_wearPerLap.TryGetValue(compound, out var list) || list.Count == 0)
        {
            return null;
        }

        return new WheelValues(list.Average(w => w.Fl), list.Average(w => w.Fr), list.Average(w => w.Rl), list.Average(w => w.Rr));
    }

    /// <summary>Laps until the most-worn tyre (by projected rate) hits the wear limit.</summary>
    public double? TyreLapsRemaining(string compound, WheelValues wear)
    {
        if (AverageWearPerLap(compound) is not { } rate)
        {
            return null;
        }

        double? best = null;
        for (var i = 0; i < 4; i++)
        {
            if (rate[i] <= 0)
            {
                continue;
            }

            var laps = Math.Max(0, (options.TyreWearLimitPercent - wear[i]) / rate[i]);
            best = best is null ? laps : Math.Min(best.Value, laps);
        }

        return best;
    }

    public double? FuelLapsRemaining(string compound, double fuelInTank) =>
        AverageFuelPerLap(compound) is > 0 and var avg ? Math.Max(0, fuelInTank / avg) : null;

    internal static WheelValues ToWheels(CarDamage d) =>
        new(d.TyresWear.FrontLeft, d.TyresWear.FrontRight, d.TyresWear.RearLeft, d.TyresWear.RearRight);

    private static bool TyreChangeDetected(WheelValues start, WheelValues end) =>
        end.Fl - start.Fl < -1 || end.Fr - start.Fr < -1 || end.Rl - start.Rl < -1 || end.Rr - start.Rr < -1;

    private static List<T> GetList<T>(Dictionary<string, List<T>> map, string key)
    {
        if (!map.TryGetValue(key, out var list))
        {
            map[key] = list = [];
        }

        return list;
    }
}
