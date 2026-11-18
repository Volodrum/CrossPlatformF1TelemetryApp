using F1Telemetry.Core.Energy;
using F1Telemetry.Core.Hub;
using F1Telemetry.Protocol;

namespace F1Telemetry.App.Services;

/// <summary>The track hub's laps as the energy planner's <see cref="PlanInputs"/>.</summary>
public static class HubPlanInputs
{
    /// <summary>See <see cref="TrackHubService.PlanLapsAsync(GameFormat, int, ErsCarModel, Func{TrackSession, bool}?)"/>.</summary>
    public static async Task<(PlanInputs? Inputs, string Unavailable)> PlanInputsAsync(this TrackHubService hub, GameFormat format, int trackId,
        ErsCarModel model, Func<TrackSession, bool>? include = null) =>
        Wrap(model, await hub.PlanLapsAsync(format, trackId, model, include));

    /// <summary>See <see cref="TrackHubService.PlanLapsAsync(ErsCarModel, IReadOnlyList{TrackSession})"/>.</summary>
    public static async Task<(PlanInputs? Inputs, string Unavailable)> PlanInputsAsync(this TrackHubService hub, ErsCarModel model, IReadOnlyList<TrackSession> sessions) =>
        Wrap(model, await hub.PlanLapsAsync(model, sessions));

    private static (PlanInputs?, string) Wrap(ErsCarModel model, (HubPlanLaps? Laps, string Unavailable) result) =>
        result.Laps is { } laps
            ? (new PlanInputs(model, laps.Race, laps.RaceLaps, laps.RaceBasis, laps.Qualifying, laps.QualifyingBasis), "")
            : (null, result.Unavailable);
}
