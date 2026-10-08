using F1Telemetry.Protocol;

namespace F1Telemetry.Simulation;

/// <summary>
/// The player's battery, closely enough to the game to exercise energy analysis: harvest under braking up to a per-lap
/// limit, deployment at full throttle by deploy mode, a 4 MJ store. The 2026 numbers are the ones measured in a real
/// F1 26 race at Spa (MGU-K 126 kW in Medium, 315 kW in Overtake falling to 135 kW above 275 km/h, 7.1 MJ harvest
/// limit). Pace does not depend on it: the speed profile drives the car.
/// <para>The driver runs Medium, Overtake through the first third of the lap from lap 2 when there is charge to spare,
/// and None from a nearly flat battery until it has recovered.</para>
/// </summary>
internal sealed class ErsModel(GameFormat format)
{
    public const double Capacity = 4_000_000;

    public const byte None = 0;
    public const byte Medium = 1;
    public const byte Hotlap = 2;
    public const byte Overtake = 3;

    private readonly bool _is2026 = format == GameFormat.F1_26;
    private int _lap = 1;

    public double Store { get; private set; } = Capacity;
    public byte Mode { get; private set; } = Medium;

    /// <summary>MGU-K output right now, W.</summary>
    public double MgukPower { get; private set; }

    /// <summary>Combustion engine output at full throttle, W.</summary>
    public double IcePower => _is2026 ? 410_000 : 600_000;

    public double HarvestLimit => _is2026 ? 7_100_000 : 2_000_000;
    public double HarvestedThisLap { get; private set; }
    public double DeployedThisLap { get; private set; }

    private double HarvestPower => _is2026 ? 350_000 : 120_000;

    public void StartLap()
    {
        _lap++;
        HarvestedThisLap = 0;
        DeployedThisLap = 0;
    }

    public void Step(double dt, double kmh, double throttle, double brake, double lapFraction)
    {
        Mode = ChooseMode(lapFraction);

        if (brake > 0)
        {
            var harvest = Math.Min(Math.Min(HarvestPower * dt, HarvestLimit - HarvestedThisLap), Capacity - Store);
            HarvestedThisLap += Math.Max(0, harvest);
            Store += Math.Max(0, harvest);
            MgukPower = 0;
            return;
        }

        var power = throttle >= 1 ? DeployPower(Mode, kmh) : 0;
        var energy = Math.Min(power * dt, Store);
        Store -= energy;
        DeployedThisLap += energy;
        MgukPower = dt > 0 ? energy / dt : 0;
    }

    private byte ChooseMode(double lapFraction)
    {
        if (Store < 300_000 || (Mode == None && Store < 1_000_000))
        {
            return None;
        }

        return _lap >= 2 && lapFraction < 1 / 3.0 && Store > 1_500_000 ? Overtake : Medium;
    }

    private double DeployPower(byte mode, double kmh) => (_is2026, mode) switch
    {
        (_, None) => 0,
        (true, Medium) => 126_000,
        (true, Hotlap) => 250_000,
        (true, _) => kmh <= 275 ? 315_000 : 135_000,
        (false, Medium) => 60_000,
        (false, _) => 120_000,
    };
}
