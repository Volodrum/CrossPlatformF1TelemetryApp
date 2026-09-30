using F1Telemetry.Protocol.Packets;

namespace F1Telemetry.Core.Engine;

/// <summary>
/// Detects new damage to the player's car: bodywork, gearbox or engine damage going up, or a new DRS / ERS fault or
/// engine failure. Tyre wear, tyre damage, blisters and brake damage are ignored because they rise steadily during a
/// stint; power-unit wear likewise. Damage going down (flashback, repair in the pits) is not new damage.
/// </summary>
public sealed class DamageMonitor
{
    private CarDamage? _last;

    public void Reset() => _last = null;

    /// <returns>True when this packet shows new damage compared with the previous one (the first packet is the baseline).</returns>
    public bool Update(CarDamage damage)
    {
        var previous = _last;
        _last = damage;
        if (previous is not { } p)
        {
            return false;
        }

        return damage.FrontLeftWingDamage > p.FrontLeftWingDamage
            || damage.FrontRightWingDamage > p.FrontRightWingDamage
            || damage.RearWingDamage > p.RearWingDamage
            || damage.FloorDamage > p.FloorDamage
            || damage.DiffuserDamage > p.DiffuserDamage
            || damage.SidepodDamage > p.SidepodDamage
            || damage.GearBoxDamage > p.GearBoxDamage
            || damage.EngineDamage > p.EngineDamage
            || (damage.DrsFault && !p.DrsFault)
            || (damage.ErsFault && !p.ErsFault)
            || (damage.EngineBlown && !p.EngineBlown)
            || (damage.EngineSeized && !p.EngineSeized);
    }
}
