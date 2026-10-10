namespace F1Telemetry.Core.Energy;

/// <summary>How a race plan treats the battery this lap.</summary>
public enum RaceStance
{
    /// <summary>End the lap with the charge the steady plan starts with.</summary>
    Normal,

    /// <summary>End the lap 1 MJ lower: spend it on a fight.</summary>
    Attack,

    /// <summary>End the lap 1 MJ higher: win back what a fight cost.</summary>
    Recover,
}

/// <summary>What to do now, at one point of a lap, against a <see cref="LapPlan"/>.</summary>
/// <param name="Zone">The deploy zone the car is in, or null between zones.</param>
/// <param name="NowMode">The mode the plan wants here; null between zones (no deployment to choose).</param>
/// <param name="OnPlan">The car runs the mode the plan wants (always true between zones).</param>
/// <param name="NextMode">The mode to switch to next: later in this zone, or at the start of the next one.</param>
/// <param name="NextAt">Where: "270 KM/H" for a speed switch, "340 M" for a distance.</param>
/// <param name="NextIn">Metres to the next switch; null for a speed switch or when there is no next mode.</param>
/// <param name="Target">The battery the plan has at this point, J.</param>
/// <param name="Delta">Battery now minus <paramref name="Target"/>, J (above 0 = more than planned).</param>
public sealed record CoachAdvice(ZonePlan? Zone, int? NowMode, bool OnPlan, int? NextMode, string NextAt, double? NextIn, double Target, double Delta);

/// <summary>Reads a <see cref="LapPlan"/> at the car's position, for the live ERS PLAN overlay.</summary>
public static class ErsCoach
{
    public static CoachAdvice Advise(LapPlan plan, double lapDistance, double store, int currentMode, double kmh)
    {
        var length = plan.Modes.Length * LapProfile.Step;
        var d = Math.Clamp(lapDistance, 0, Math.Max(0, length - 1));
        var segment = Math.Clamp((int)(d / LapProfile.Step), 0, plan.Stores.Length - 1);
        var target = plan.Stores[segment];
        var zone = plan.Zones.FirstOrDefault(z => d >= z.From && d < z.To);

        int? now = null;
        int? next = null;
        var nextAt = "";
        double? nextIn = null;
        if (zone is not null)
        {
            var choice = zone.Choice;
            var switched = false;
            if (choice.First != choice.Then)
            {
                if (choice.UntilKmh is { } kmhLimit)
                {
                    switched = kmh >= kmhLimit;
                    if (!switched)
                    {
                        (next, nextAt) = (choice.Then, $"{kmhLimit} KM/H");
                    }
                }
                else if (choice.UntilFraction is { } fraction)
                {
                    var at = zone.From + fraction * (zone.To - zone.From);
                    switched = d >= at;
                    if (!switched)
                    {
                        (next, nextAt, nextIn) = (choice.Then, Metres(at - d), at - d);
                    }
                }
            }

            now = switched ? choice.Then : choice.First;
        }

        if (next is null && NextZone(plan, d) is { } upcoming)
        {
            var distance = upcoming.From > d ? upcoming.From - d : upcoming.From + length - d;
            (next, nextAt, nextIn) = (upcoming.Choice.First, Metres(distance), distance);
        }

        return new CoachAdvice(zone, now, now is null || now == currentMode, next, nextAt, nextIn, target, store - target);
    }

    /// <summary>The first zone that starts after <paramref name="d"/>, wrapping to the first one of the next lap.</summary>
    private static ZonePlan? NextZone(LapPlan plan, double d) =>
        plan.Zones.FirstOrDefault(z => z.From > d) ?? plan.Zones.FirstOrDefault();

    private static string Metres(double metres) => $"{Math.Max(0, Math.Round(metres / 10) * 10):0} M";
}
