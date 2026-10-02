using System.Buffers.Binary;
using System.Text;
using F1Telemetry.Protocol;
using F1Telemetry.Protocol.Packets;

namespace F1Telemetry.Simulation;

/// <summary>
/// Builds spec-exact datagrams using explicit byte offsets (deliberately independent from the sequential
/// parser, so round-trip tests catch layout mistakes on either side).
/// </summary>
public sealed class PacketWriter(FormatLayout layout)
{
    private const int H = FormatLayout.HeaderSize;

    public FormatLayout Layout => layout;

    public byte[] Create(PacketId id, ulong sessionUid, float sessionTime, uint frame, byte playerIndex)
    {
        var buffer = new byte[layout.PacketSizes[id]];
        var span = buffer.AsSpan();
        BinaryPrimitives.WriteUInt16LittleEndian(span, (ushort)layout.Format);
        span[2] = 25; // gameYear
        span[3] = 1;
        span[4] = 0;
        span[5] = 1;
        span[6] = (byte)id;
        BinaryPrimitives.WriteUInt64LittleEndian(span[7..], sessionUid);
        BinaryPrimitives.WriteSingleLittleEndian(span[15..], sessionTime);
        BinaryPrimitives.WriteUInt32LittleEndian(span[19..], frame);
        BinaryPrimitives.WriteUInt32LittleEndian(span[23..], frame);
        span[27] = playerIndex;
        span[28] = 255;
        return buffer;
    }

    public void WriteMotion(byte[] packet, int car, CarMotion m)
    {
        var s = Slot(packet, car, layout.MotionSlot);
        F32(s, 0, m.WorldPositionX);
        F32(s, 4, m.WorldPositionY);
        F32(s, 8, m.WorldPositionZ);
        F32(s, 12, m.WorldVelocityX);
        F32(s, 16, m.WorldVelocityY);
        F32(s, 20, m.WorldVelocityZ);
        N16(s, 24, m.ForwardX);
        N16(s, 26, m.ForwardY);
        N16(s, 28, m.ForwardZ);
        N16(s, 30, m.RightX);
        N16(s, 32, m.RightY);
        N16(s, 34, m.RightZ);
        int angles;
        if (layout.QuantisedGForces)
        {
            I16(s, 36, (short)Math.Round(m.GForceLateral * 1000));
            I16(s, 38, (short)Math.Round(m.GForceLongitudinal * 1000));
            I16(s, 40, (short)Math.Round(m.GForceVertical * 1000));
            angles = 42;
        }
        else
        {
            F32(s, 36, m.GForceLateral);
            F32(s, 40, m.GForceLongitudinal);
            F32(s, 44, m.GForceVertical);
            angles = 48;
        }

        F32(s, angles, m.Yaw);
        F32(s, angles + 4, m.Pitch);
        F32(s, angles + 8, m.Roll);
    }

    public void WriteLapData(byte[] packet, int car, LapData d)
    {
        var s = Slot(packet, car, layout.LapSlot);
        U32(s, 0, d.LastLapTimeMs);
        U32(s, 4, d.CurrentLapTimeMs);
        SplitTime(s, 8, d.Sector1TimeMs);
        SplitTime(s, 11, d.Sector2TimeMs);
        SplitTime(s, 14, d.DeltaToCarInFrontMs);
        SplitTime(s, 17, d.DeltaToRaceLeaderMs);
        F32(s, 20, d.LapDistance);
        F32(s, 24, d.TotalDistance);
        F32(s, 28, d.SafetyCarDelta);
        s[32] = d.CarPosition;
        s[33] = d.CurrentLapNum;
        s[34] = (byte)d.PitStatus;
        s[35] = d.NumPitStops;
        s[36] = d.Sector;
        s[37] = d.CurrentLapInvalid ? (byte)1 : (byte)0;
        s[38] = d.Penalties;
        s[39] = d.TotalWarnings;
        s[40] = d.CornerCuttingWarnings;
        s[43] = d.GridPosition;
        s[44] = (byte)d.DriverStatus;
        s[45] = (byte)d.ResultStatus;
        s[46] = d.PitLaneTimerActive ? (byte)1 : (byte)0;
        U16(s, 47, d.PitLaneTimeInLaneMs);
        U16(s, 49, d.PitStopTimerMs);
        F32(s, 52, d.SpeedTrapFastestSpeed);
    }

    public void WriteCarTelemetry(byte[] packet, int car, CarTelemetry t)
    {
        var s = Slot(packet, car, layout.TelemetrySlot);
        U16(s, 0, t.Speed);
        F32(s, 2, t.Throttle);
        F32(s, 6, t.Steer);
        F32(s, 10, t.Brake);
        s[14] = t.Clutch;
        s[15] = unchecked((byte)t.Gear);
        U16(s, 16, t.EngineRpm);
        s[18] = t.Drs ? (byte)1 : (byte)0;
        s[19] = t.RevLightsPercent;
        TyresU16(s, 22, t.BrakesTemperature);
        TyresU8(s, 30, t.TyresSurfaceTemperature);
        TyresU8(s, 34, t.TyresInnerTemperature);
        int pressures;
        if (layout.ByteEngineTemperature)
        {
            s[38] = (byte)Math.Min(t.EngineTemperature, (ushort)255);
            pressures = 39;
        }
        else
        {
            U16(s, 38, t.EngineTemperature);
            pressures = 40;
        }

        TyresF32(s, pressures, t.TyresPressure);
        TyresU8(s, pressures + 16, t.SurfaceType);
    }

    public void WriteCarTelemetryTrailer(byte[] packet, byte mfdPanel, sbyte suggestedGear)
    {
        var offset = H + layout.MaxCars * layout.TelemetrySlot;
        packet[offset] = mfdPanel;
        packet[offset + 1] = 255;
        packet[offset + 2] = unchecked((byte)suggestedGear);
    }

    public void WriteCarStatus(byte[] packet, int car, CarStatus c)
    {
        var s = Slot(packet, car, layout.StatusSlot);
        s[0] = c.TractionControl;
        s[1] = c.AntiLockBrakes;
        s[2] = c.FuelMix;
        s[3] = c.FrontBrakeBias;
        s[4] = c.PitLimiter ? (byte)1 : (byte)0;
        F32(s, 5, c.FuelInTank);
        F32(s, 9, c.FuelCapacity);
        F32(s, 13, c.FuelRemainingLaps);
        U16(s, 17, c.MaxRpm);
        U16(s, 19, c.IdleRpm);
        s[21] = c.MaxGears;
        s[22] = c.DrsAllowed ? (byte)1 : (byte)0;
        U16(s, 23, c.DrsActivationDistance);
        s[25] = c.ActualTyreCompound;
        s[26] = c.VisualTyreCompound;
        s[27] = c.TyresAgeLaps;
        s[28] = unchecked((byte)c.VehicleFiaFlags);
        F32(s, 29, c.EnginePowerIce);
        F32(s, 33, c.EnginePowerMguk);
        F32(s, 37, c.ErsStoreEnergy);
        s[41] = c.ErsDeployMode;
        F32(s, 42, c.ErsHarvestedThisLapMguk);
        F32(s, 46, c.ErsHarvestedThisLapMguh);
        if (layout.HasErsHarvestLimit)
        {
            F32(s, 50, c.ErsHarvestLimitPerLap ?? 0);
            F32(s, 54, c.ErsDeployedThisLap);
            s[58] = c.NetworkPaused ? (byte)1 : (byte)0;
        }
        else
        {
            F32(s, 50, c.ErsDeployedThisLap);
            s[54] = c.NetworkPaused ? (byte)1 : (byte)0;
        }
    }

    public void WriteCarDamage(byte[] packet, int car, CarDamage d)
    {
        var s = Slot(packet, car, layout.DamageSlot);
        TyresF32(s, 0, d.TyresWear);
        TyresU8(s, 16, d.TyresDamage);
        TyresU8(s, 20, d.BrakesDamage);
        TyresU8(s, 24, d.TyreBlisters);
        s[28] = d.FrontLeftWingDamage;
        s[29] = d.FrontRightWingDamage;
        s[30] = d.RearWingDamage;
        s[31] = d.FloorDamage;
        s[32] = d.DiffuserDamage;
        s[33] = d.SidepodDamage;
        s[34] = d.DrsFault ? (byte)1 : (byte)0;
        s[35] = d.ErsFault ? (byte)1 : (byte)0;
        s[36] = d.GearBoxDamage;
        s[37] = d.EngineDamage;
        s[38] = d.EngineMguhWear;
        s[39] = d.EngineEsWear;
        s[40] = d.EngineCeWear;
        s[41] = d.EngineIceWear;
        s[42] = d.EngineMgukWear;
        s[43] = d.EngineTcWear;
        s[44] = d.EngineBlown ? (byte)1 : (byte)0;
        s[45] = d.EngineSeized ? (byte)1 : (byte)0;
    }

    public void WriteCarTelemetry2(byte[] packet, int car, CarTelemetry2 t)
    {
        var s = Slot(packet, car, layout.Telemetry2Slot);
        s[0] = t.ActiveAeroMode;
        s[1] = t.ActiveAeroAvailable ? (byte)1 : (byte)0;
        U16(s, 2, t.ActiveAeroActivationDistance);
        s[4] = t.OvertakeAvailable ? (byte)1 : (byte)0;
        s[5] = t.OvertakeActive ? (byte)1 : (byte)0;
        U16(s, 6, t.OvertakeActivationDistance);
        s[8] = t.Regulations2026Applicable ? (byte)1 : (byte)0;
        s[9] = t.IsDrivingWrongWay ? (byte)1 : (byte)0;
    }

    public void WriteSession(byte[] packet, SessionData d)
    {
        var s = packet.AsSpan(H);
        s[0] = d.Weather;
        s[1] = unchecked((byte)d.TrackTemperature);
        s[2] = unchecked((byte)d.AirTemperature);
        s[3] = d.TotalLaps;
        U16(s, 4, d.TrackLength);
        s[6] = d.SessionType;
        s[7] = unchecked((byte)d.TrackId);
        s[8] = d.Formula;
        U16(s, 9, d.SessionTimeLeft);
        U16(s, 11, d.SessionDuration);
        s[13] = d.PitSpeedLimit;
        s[14] = d.GamePaused ? (byte)1 : (byte)0;
        s[124] = d.SafetyCarStatus;
        s[125] = d.IsNetworkGame ? (byte)1 : (byte)0;
        s[126] = (byte)Math.Min(d.WeatherForecast.Length, 64);
        for (var i = 0; i < Math.Min(d.WeatherForecast.Length, 64); i++)
        {
            var w = d.WeatherForecast[i];
            var o = 127 + i * 8;
            s[o] = w.SessionType;
            s[o + 1] = w.TimeOffsetMinutes;
            s[o + 2] = w.Weather;
            s[o + 3] = unchecked((byte)w.TrackTemperature);
            s[o + 4] = unchecked((byte)w.TrackTemperatureChange);
            s[o + 5] = unchecked((byte)w.AirTemperature);
            s[o + 6] = unchecked((byte)w.AirTemperatureChange);
            s[o + 7] = w.RainPercentage;
        }

        s[639] = d.ForecastAccuracy;
        s[640] = d.AiDifficulty;
        s[653] = d.PitStopWindowIdealLap;
        s[654] = d.PitStopWindowLatestLap;
        s[655] = d.PitStopRejoinPosition;
        s[676] = d.NumSafetyCarPeriods;
        s[677] = d.NumVirtualSafetyCarPeriods;
        s[678] = d.NumRedFlagPeriods;
        F32(s, 716, d.Sector2LapDistanceStart);
        F32(s, 720, d.Sector3LapDistanceStart);

        if (layout.HasSessionAeroBlock && d.Aero is { } aero)
        {
            s[724] = aero.ActiveAeroTrackStatus;
            WriteZones(s, 725, aero.ActiveAeroZonesFull, 8);
            WriteZones(s, 790, aero.ActiveAeroZonesPartial, 8);
            WriteZones(s, 855, aero.DrsZones, 4);
            F32(s, 888, aero.StartReactionTime);
        }
    }

    public void WriteSessionHistory(byte[] packet, byte carIndex, IReadOnlyList<LapHistory> laps, IReadOnlyList<TyreStint> stints,
        byte bestLap, byte bestS1, byte bestS2, byte bestS3)
    {
        var s = packet.AsSpan(H);
        s[0] = carIndex;
        s[1] = (byte)Math.Min(laps.Count, 100);
        s[2] = (byte)Math.Min(stints.Count, 8);
        s[3] = bestLap;
        s[4] = bestS1;
        s[5] = bestS2;
        s[6] = bestS3;
        for (var i = 0; i < Math.Min(laps.Count, 100); i++)
        {
            var o = 7 + i * 14;
            U32(s, o, laps[i].LapTimeMs);
            SplitTime(s, o + 4, laps[i].Sector1Ms);
            SplitTime(s, o + 7, laps[i].Sector2Ms);
            SplitTime(s, o + 10, laps[i].Sector3Ms);
            s[o + 13] = laps[i].ValidFlags;
        }

        for (var i = 0; i < Math.Min(stints.Count, 8); i++)
        {
            var o = 7 + 100 * 14 + i * 3;
            s[o] = stints[i].EndLap;
            s[o + 1] = stints[i].ActualCompound;
            s[o + 2] = stints[i].VisualCompound;
        }
    }

    public void WriteFinalClassification(byte[] packet, int car, FinalClassification c)
    {
        packet[H] = (byte)layout.MaxCars;
        var s = packet.AsSpan(H + 1 + car * layout.FinalClassificationSlot, layout.FinalClassificationSlot);
        s[0] = c.Position;
        s[1] = c.NumLaps;
        s[2] = c.GridPosition;
        s[3] = c.Points;
        s[4] = c.NumPitStops;
        s[5] = (byte)c.ResultStatus;
        U32(s, 7, c.BestLapTimeMs);
        BinaryPrimitives.WriteDoubleLittleEndian(s[11..], c.TotalRaceTime);
        s[19] = c.PenaltiesTime;
    }

    public void WriteNumActiveCars(byte[] packet, byte count) => packet[H] = count;

    public void WriteParticipant(byte[] packet, int car, Participant p)
    {
        var s = packet.AsSpan(H + 1 + car * layout.ParticipantSlot, layout.ParticipantSlot);
        s[0] = p.AiControlled ? (byte)1 : (byte)0;
        int o;
        if (layout.WideParticipantIds)
        {
            U16(s, 1, p.DriverId);
            U16(s, 3, p.NetworkId);
            U16(s, 5, p.TeamId);
            o = 7;
        }
        else
        {
            s[1] = (byte)p.DriverId;
            s[2] = (byte)p.NetworkId;
            s[3] = (byte)p.TeamId;
            o = 4;
        }

        s[o] = p.MyTeam ? (byte)1 : (byte)0;
        s[o + 1] = p.RaceNumber;
        s[o + 2] = p.Nationality;
        var name = s.Slice(o + 3, 32);
        name.Clear();
        Encoding.UTF8.GetBytes(p.Name.AsSpan(0, Math.Min(p.Name.Length, 31)), name);
        s[o + 35] = p.TelemetryPublic ? (byte)1 : (byte)0;
        s[o + 36] = p.ShowOnlineNames ? (byte)1 : (byte)0;
        U16(s, o + 37, p.TechLevel);
        s[o + 39] = p.Platform;
        s[o + 40] = (byte)Math.Min(p.LiveryColours.Length, 4);
        for (var i = 0; i < Math.Min(p.LiveryColours.Length, 4); i++)
        {
            s[o + 41 + i * 3] = p.LiveryColours[i].Red;
            s[o + 42 + i * 3] = p.LiveryColours[i].Green;
            s[o + 43 + i * 3] = p.LiveryColours[i].Blue;
        }
    }

    public void WriteEventCode(byte[] packet, string code) =>
        Encoding.ASCII.GetBytes(code.AsSpan(0, 4), packet.AsSpan(H, 4));

    /// <summary>A <c>BUTN</c> event: code plus the button status bit field.</summary>
    public void WriteButtons(byte[] packet, uint buttonStatus)
    {
        WriteEventCode(packet, EventPacket.Buttons);
        U32(packet.AsSpan(H), 4, buttonStatus);
    }

    private Span<byte> Slot(byte[] packet, int car, int slotSize) => packet.AsSpan(H + car * slotSize, slotSize);

    private static void WriteZones(Span<byte> s, int offset, TrackZone[] zones, int capacity)
    {
        s[offset] = (byte)Math.Min(zones.Length, capacity);
        for (var i = 0; i < Math.Min(zones.Length, capacity); i++)
        {
            F32(s, offset + 1 + i * 8, zones[i].Start);
            F32(s, offset + 5 + i * 8, zones[i].End);
        }
    }

    private static void SplitTime(Span<byte> s, int offset, uint ms)
    {
        U16(s, offset, (ushort)(ms % 60_000));
        s[offset + 2] = (byte)(ms / 60_000);
    }

    private static void TyresU8(Span<byte> s, int o, Tyres<byte> t)
    {
        s[o] = t.RearLeft;
        s[o + 1] = t.RearRight;
        s[o + 2] = t.FrontLeft;
        s[o + 3] = t.FrontRight;
    }

    private static void TyresU16(Span<byte> s, int o, Tyres<ushort> t)
    {
        U16(s, o, t.RearLeft);
        U16(s, o + 2, t.RearRight);
        U16(s, o + 4, t.FrontLeft);
        U16(s, o + 6, t.FrontRight);
    }

    private static void TyresF32(Span<byte> s, int o, Tyres<float> t)
    {
        F32(s, o, t.RearLeft);
        F32(s, o + 4, t.RearRight);
        F32(s, o + 8, t.FrontLeft);
        F32(s, o + 12, t.FrontRight);
    }

    private static void U16(Span<byte> s, int o, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(s[o..], v);
    private static void I16(Span<byte> s, int o, short v) => BinaryPrimitives.WriteInt16LittleEndian(s[o..], v);
    private static void U32(Span<byte> s, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(s[o..], v);
    private static void F32(Span<byte> s, int o, float v) => BinaryPrimitives.WriteSingleLittleEndian(s[o..], v);
    private static void N16(Span<byte> s, int o, float v) => I16(s, o, (short)Math.Round(Math.Clamp(v, -1f, 1f) * 32767f));
}
