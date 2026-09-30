using F1Telemetry.Core.Models;
using F1Telemetry.Protocol.Lookups;
using F1Telemetry.Protocol.Packets;

namespace F1Telemetry.Core.Engine;

/// <summary>
/// Live classification of laps. States are sticky with priority PIT &gt; SC &gt; VSC &gt; REGULAR: once a lap
/// has seen the pit lane or a safety car it keeps that label.
/// </summary>
public sealed class LapTypeTracker
{
    private readonly Dictionary<int, LapType> _types = [];

    public void Reset() => _types.Clear();

    public LapType Get(int lap) => _types.GetValueOrDefault(lap, LapType.Regular);

    public IReadOnlyDictionary<int, LapType> All => _types;

    public void OnSafetyCar(int lap, byte safetyCarStatus)
    {
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
        LapType.Pit => 3,
        LapType.SafetyCar => 2,
        LapType.VirtualSafetyCar => 1,
        _ => 0,
    };
}
