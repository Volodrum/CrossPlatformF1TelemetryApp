using F1Telemetry.Core.Models;
using F1Telemetry.Protocol.Lookups;
using F1Telemetry.Protocol.Packets;

namespace F1Telemetry.Core.Engine;

/// <summary>
/// Live classification of laps. States are sticky with priority RED FLAG &gt; PIT &gt; SC &gt; VSC &gt; REGULAR: once a
/// lap has seen a red flag, the pit lane or a safety car it keeps that label. A red flag outranks the pit lane because
/// the field drives into it when the race is stopped.
/// </summary>
public sealed class LapTypeTracker
{
    private readonly Dictionary<int, LapType> _types = [];
    private readonly Dictionary<int, Neutralisation> _neutralisations = [];

    public void Reset()
    {
        _types.Clear();
        _neutralisations.Clear();
    }

    public LapType Get(int lap) => _types.GetValueOrDefault(lap, LapType.Regular);

    public IReadOnlyDictionary<int, LapType> All => _types;

    /// <summary>The strongest safety car seen on the lap, kept apart from its type so a pit stop under one still shows it.</summary>
    public Neutralisation GetNeutralisation(int lap) => _neutralisations.GetValueOrDefault(lap);

    public void OnSafetyCar(int lap, byte safetyCarStatus)
    {
        var neutralisation = safetyCarStatus switch
        {
            SafetyCarStatus.Full => Neutralisation.SafetyCar,
            SafetyCarStatus.Virtual => Neutralisation.VirtualSafetyCar,
            _ => Neutralisation.None,
        };
        if (lap > 0 && neutralisation > GetNeutralisation(lap))
        {
            _neutralisations[lap] = neutralisation;
        }

        switch (safetyCarStatus)
        {
            case SafetyCarStatus.Full:
                Promote(lap, LapType.SafetyCar);
                break;
            case SafetyCarStatus.Virtual:
                Promote(lap, LapType.VirtualSafetyCar);
                break;
        }
    }

    public void OnRedFlag(int lap) => Promote(lap, LapType.RedFlag);

    public void OnPitStatus(int lap, PitStatus pitStatus)
    {
        if (pitStatus != PitStatus.None)
        {
            Promote(lap, LapType.Pit);
        }
    }

    private void Promote(int lap, LapType candidate)
    {
        if (lap <= 0)
        {
            return;
        }

        var current = Get(lap);
        if (Priority(candidate) > Priority(current))
        {
            _types[lap] = candidate;
        }
    }

    private static int Priority(LapType type) => type switch
    {
        LapType.RedFlag => 4,
        LapType.Pit => 3,
        LapType.SafetyCar => 2,
        LapType.VirtualSafetyCar => 1,
        _ => 0,
    };
}
