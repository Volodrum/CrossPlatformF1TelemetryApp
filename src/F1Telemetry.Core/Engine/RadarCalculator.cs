using F1Telemetry.Core.Models;
using F1Telemetry.Protocol.Packets;

namespace F1Telemetry.Core.Engine;

/// <summary>Projects nearby cars into the player's local frame for the proximity radar.</summary>
public static class RadarCalculator
{
    public const float Range = 25.2f;
    private const float DangerDistance = 4.0f;
    private const float NearDistance = 8.0f;
    private const float SideBySideLongitudinal = 3.5f;

    public static RadarFrame Compute(MotionPacket motion, LapDataPacket? lapData)
    {
        var playerIdx = motion.Header.PlayerCarIndex;
        if (playerIdx >= motion.Cars.Length)
        {
            return new RadarFrame([], false, false, Range);
        }

        var player = motion.Cars[playerIdx];
        var playerYaw = MathF.Atan2(player.ForwardX, player.ForwardZ);
        var blips = new List<RadarBlip>();
        bool left = false, right = false;

        for (var i = 0; i < motion.Cars.Length; i++)
        {
            if (i == playerIdx)
            {
                continue;
            }

            var car = motion.Cars[i];
            if (car.WorldPositionX == 0 && car.WorldPositionZ == 0)
            {
                continue;
            }

            if (lapData is not null && lapData.Cars[i].ResultStatus is not (ResultStatus.Active or ResultStatus.Finished))
            {
                continue;
            }

            var dx = car.WorldPositionX - player.WorldPositionX;
            var dz = car.WorldPositionZ - player.WorldPositionZ;
            var distance = MathF.Sqrt(dx * dx + dz * dz);
            if (distance >= Range)
            {
                continue;
            }

            var relZ = dx * player.ForwardX + dz * player.ForwardZ;
            var relX = dx * player.RightX + dz * player.RightZ;
            var relYaw = (playerYaw - MathF.Atan2(car.ForwardX, car.ForwardZ)) * (180f / MathF.PI);

            var severity = distance < DangerDistance ? RadarSeverity.Danger
                : distance < NearDistance ? RadarSeverity.Near
                : RadarSeverity.Far;

            if (severity == RadarSeverity.Danger && MathF.Abs(relZ) < SideBySideLongitudinal)
            {
                if (relX < 0)
                {
                    left = true;
                }
                else
                {
                    right = true;
                }
            }

            blips.Add(new RadarBlip(i, relX, relZ, relYaw, distance, severity));
        }

        return new RadarFrame(blips, left, right, Range);
    }
}
