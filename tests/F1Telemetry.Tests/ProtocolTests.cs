using F1Telemetry.Protocol;
using F1Telemetry.Protocol.Packets;
using F1Telemetry.Simulation;

namespace F1Telemetry.Tests;

public class ProtocolTests
{
    public static TheoryData<GameFormat> Formats => [GameFormat.F1_25, GameFormat.F1_26];

    [Theory]
    [MemberData(nameof(Formats))]
    public void Every_known_packet_size_parses(GameFormat format)
    {
        var layout = FormatLayout.For(format);
        var writer = new PacketWriter(layout);
        foreach (var id in layout.PacketSizes.Keys)
        {
            var result = PacketParser.Parse(writer.Create(id, 1, 0, 0, 0));
            Assert.True(result.IsSuccess, $"{format} {id}: {result.Status}");
            Assert.Equal(id, result.Header!.Value.PacketId);
        }
    }

    [Fact]
    public void Slot_layouts_match_documented_packet_sizes()
    {
        foreach (var layout in FormatLayout.All)
        {
            var h = FormatLayout.HeaderSize;
            Assert.Equal(layout.PacketSizes[PacketId.Motion], h + layout.MaxCars * layout.MotionSlot);
            Assert.Equal(layout.PacketSizes[PacketId.LapData], h + layout.MaxCars * layout.LapSlot + 2);
            Assert.Equal(layout.PacketSizes[PacketId.CarTelemetry], h + layout.MaxCars * layout.TelemetrySlot + 3);
            Assert.Equal(layout.PacketSizes[PacketId.CarStatus], h + layout.MaxCars * layout.StatusSlot);
            Assert.Equal(layout.PacketSizes[PacketId.CarDamage], h + layout.MaxCars * layout.DamageSlot);
            Assert.Equal(layout.PacketSizes[PacketId.CarSetups], h + layout.MaxCars * layout.SetupSlot + 4);
            Assert.Equal(layout.PacketSizes[PacketId.FinalClassification], h + 1 + layout.MaxCars * layout.FinalClassificationSlot);
        }

        Assert.Equal(FormatLayout.HeaderSize + 24 * 10, FormatLayout.F1_26.PacketSizes[PacketId.CarTelemetry2]);
    }

    [Fact]
    public void Rejects_wrong_size_and_unknown_format()
    {
        var writer = new PacketWriter(FormatLayout.F1_25);
        var packet = writer.Create(PacketId.CarTelemetry, 1, 0, 0, 0);

        Assert.Equal(ParseStatus.SizeMismatch, PacketParser.Parse(packet.AsSpan(0, packet.Length - 1)).Status);

        packet[0] = 0xE8; // 2024
        packet[1] = 0x07;
        Assert.Equal(ParseStatus.UnsupportedFormat, PacketParser.Parse(packet).Status);
        Assert.Equal(ParseStatus.TooShort, PacketParser.Parse(new byte[10]).Status);
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void Header_round_trips(GameFormat format)
    {
        var writer = new PacketWriter(FormatLayout.For(format));
        var header = PacketParser.Parse(writer.Create(PacketId.LapData, 0xDEADBEEF12345678, 123.5f, 42, 3)).Header!.Value;
        Assert.Equal(format, header.Format);
        Assert.Equal(0xDEADBEEF12345678UL, header.SessionUid);
        Assert.Equal(123.5f, header.SessionTime);
        Assert.Equal(42u, header.FrameIdentifier);
        Assert.Equal(3, header.PlayerCarIndex);
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void Motion_round_trips_including_quantised_gforces(GameFormat format)
    {
        var writer = new PacketWriter(FormatLayout.For(format));
        var packet = writer.Create(PacketId.Motion, 1, 0, 0, 1);
        var expected = new CarMotion(100.5f, 2, -300.25f, 50, 0, 10, 0.6f, 0, 0.8f, 0.8f, 0, -0.6f, 3.456f, -4.5f, 1.25f, 0.5f, 0.01f, -0.02f);
        writer.WriteMotion(packet, 1, expected);

        var parsed = Assert.IsType<MotionPacket>(PacketParser.Parse(packet).Packet).Player;
        Assert.Equal(expected.WorldPositionX, parsed.WorldPositionX);
        Assert.Equal(expected.WorldPositionZ, parsed.WorldPositionZ);
        Assert.Equal(expected.ForwardX, parsed.ForwardX, 3);
        Assert.Equal(expected.RightZ, parsed.RightZ, 3);
        Assert.Equal(expected.GForceLateral, parsed.GForceLateral, 3);
        Assert.Equal(expected.GForceLongitudinal, parsed.GForceLongitudinal, 3);
        Assert.Equal(expected.Yaw, parsed.Yaw);
        Assert.Equal(expected.Roll, parsed.Roll);
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void Lap_data_round_trips(GameFormat format)
    {
        var layout = FormatLayout.For(format);
        var writer = new PacketWriter(layout);
        var packet = writer.Create(PacketId.LapData, 1, 0, 0, (byte)(layout.MaxCars - 1));
        var expected = new LapData(83_456, 12_345, 61_234, 28_001, 850, 64_000, 1234.5f, 9999, 0, 5, 7, PitStatus.Pitting, 1, 2, true, 3, 2, 1,
            8, DriverStatus.InLap, ResultStatus.Active, true, 1500, 2300, 321.5f);
        writer.WriteLapData(packet, layout.MaxCars - 1, expected);

        var parsed = Assert.IsType<LapDataPacket>(PacketParser.Parse(packet).Packet).Player;
        Assert.Equal(expected, parsed);
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void Car_telemetry_round_trips(GameFormat format)
    {
        var writer = new PacketWriter(FormatLayout.For(format));
        var packet = writer.Create(PacketId.CarTelemetry, 1, 0, 0, 0);
        var expected = new CarTelemetry(312, 1, -0.25f, 0.5f, 0, 7, 11_800, true, 90,
            new Tyres<ushort>(500, 510, 620, 630), new Tyres<byte>(90, 91, 92, 93), new Tyres<byte>(100, 101, 102, 103), 112,
            new Tyres<float>(22.1f, 22.2f, 23.3f, 23.4f), new Tyres<byte>(0, 1, 2, 3));
        writer.WriteCarTelemetry(packet, 0, expected);
        writer.WriteCarTelemetryTrailer(packet, 4, 6);

        var parsed = Assert.IsType<CarTelemetryPacket>(PacketParser.Parse(packet).Packet);
        Assert.Equal(expected, parsed.Player);
        Assert.Equal(6, parsed.SuggestedGear);
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void Car_status_round_trips(GameFormat format)
    {
        var layout = FormatLayout.For(format);
        var writer = new PacketWriter(layout);
        var packet = writer.Create(PacketId.CarStatus, 1, 0, 0, 2);
        var expected = new CarStatus(1, 1, 2, 55, false, 42.5f, 110, 12.3f, 12_500, 4000, 8, true, 0, 18, 17, 4, 0,
            600_000, 120_000, 3_500_000, 2, 400_000, 200_000, layout.HasErsHarvestLimit ? 6_000_000f : null, 350_000, false);
        writer.WriteCarStatus(packet, 2, expected);

        var parsed = Assert.IsType<CarStatusPacket>(PacketParser.Parse(packet).Packet).Player;
        Assert.Equal(expected, parsed);
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void Car_damage_round_trips(GameFormat format)
    {
        var writer = new PacketWriter(FormatLayout.For(format));
        var packet = writer.Create(PacketId.CarDamage, 1, 0, 0, 0);
        var expected = new CarDamage(new Tyres<float>(10.5f, 11, 12, 13), new Tyres<byte>(1, 2, 3, 4), new Tyres<byte>(5, 6, 7, 8),
            new Tyres<byte>(9, 10, 11, 12), 13, 14, 15, 16, 17, 18, true, false, 19, 20, 21, 22, 23, 24, 25, 26, false, true);
        writer.WriteCarDamage(packet, 0, expected);
        Assert.Equal(expected, Assert.IsType<CarDamagePacket>(PacketParser.Parse(packet).Packet).Player);
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void Participants_round_trip_including_wide_2026_ids(GameFormat format)
    {
        var layout = FormatLayout.For(format);
        Assert.Equal(layout.PacketSizes[PacketId.Participants], FormatLayout.HeaderSize + 1 + layout.MaxCars * layout.ParticipantSlot);

        var writer = new PacketWriter(layout);
        var packet = writer.Create(PacketId.Participants, 1, 0, 0, 0);
        var teamId = format == GameFormat.F1_26 ? (ushort)485 : (ushort)8;
        var expected = new Participant(true, 57, 3, teamId, false, 4, 12, "Kimi ANTONELLI", true, false, 1234, 3,
            [new LiveryColour(255, 128, 0), new LiveryColour(1, 2, 3)]);
        writer.WriteNumActiveCars(packet, 20);
        writer.WriteParticipant(packet, 0, expected with { Name = "Player", AiControlled = false });
        writer.WriteParticipant(packet, layout.MaxCars - 1, expected);

        var parsed = Assert.IsType<ParticipantsPacket>(PacketParser.Parse(packet).Packet);
        Assert.Equal(20, parsed.NumActiveCars);
        Assert.Equal("Player", parsed.Cars[0].Name);
        var last = parsed.Cars[^1];
        Assert.Equal(expected.LiveryColours, last.LiveryColours);
        Assert.Equal(expected with { LiveryColours = last.LiveryColours }, last);
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void Button_event_decodes_udp_actions(GameFormat format)
    {
        var writer = new PacketWriter(FormatLayout.For(format));
        var packet = writer.Create(PacketId.Event, 1, 0, 0, 0);
        writer.WriteButtons(packet, EventPacket.UdpActionMask(1) | EventPacket.UdpActionMask(12) | 0x1);

        var evt = Assert.IsType<EventPacket>(PacketParser.Parse(packet).Packet);
        Assert.Equal(EventPacket.Buttons, evt.Code);
        Assert.Equal(0x80100001u, evt.ButtonStatus);
        Assert.Equal(0x00100000u, EventPacket.UdpActionMask(1));
        Assert.Equal(0u, EventPacket.UdpActionMask(0));

        var other = writer.Create(PacketId.Event, 1, 0, 0, 0);
        writer.WriteEventCode(other, EventPacket.Flashback);
        Assert.Null(Assert.IsType<EventPacket>(PacketParser.Parse(other).Packet).ButtonStatus);
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void Session_round_trips(GameFormat format)
    {
        var layout = FormatLayout.For(format);
        var writer = new PacketWriter(layout);
        var packet = writer.Create(PacketId.Session, 1, 0, 0, 0);
        var aero = new SessionAeroInfo(1, [new TrackZone(0.1f, 0.2f)], [], [new TrackZone(0.5f, 0.6f), new TrackZone(0.8f, 0.9f)], 0.25f);
        var expected = new SessionData(3, 35, -2, 58, 5793, 15, 11, 13, 1200, 3600, 80, false, 2, true,
            [new WeatherForecastSample(15, 0, 3, 30, 1, 20, 2, 40), new WeatherForecastSample(15, 10, 4, 28, 1, 19, 1, 80)],
            1, 95, 20, 25, 7, 1, 2, 0, 1900.5f, 3800.25f, layout.HasSessionAeroBlock ? aero : null);
        writer.WriteSession(packet, expected);

        var parsed = Assert.IsType<SessionPacket>(PacketParser.Parse(packet).Packet).Data;
        Assert.Equal(expected with { WeatherForecast = [], Aero = null }, parsed with { WeatherForecast = [], Aero = null });
        Assert.Equal(expected.WeatherForecast, parsed.WeatherForecast);
        if (layout.HasSessionAeroBlock)
        {
            Assert.NotNull(parsed.Aero);
            Assert.Equal(aero.ActiveAeroZonesFull, parsed.Aero.ActiveAeroZonesFull);
            Assert.Equal(aero.DrsZones, parsed.Aero.DrsZones);
            Assert.Equal(aero.StartReactionTime, parsed.Aero.StartReactionTime);
        }
        else
        {
            Assert.Null(parsed.Aero);
        }
    }

    [Fact]
    public void Session_history_round_trips_and_maps_stints()
    {
        var writer = new PacketWriter(FormatLayout.F1_25);
        var packet = writer.Create(PacketId.SessionHistory, 1, 0, 0, 0);
        LapHistory[] laps = [new(90_000, 30_000, 31_000, 29_000, 0x0F), new(89_500, 61_500, 30_000, 0, 0x0F), new(0, 29_800, 0, 0, 0x0F)];
        TyreStint[] stints = [new(2, 18, 17), new(TyreStint.Current, 19, 18)];
        writer.WriteSessionHistory(packet, 0, laps, stints, 2, 3, 2, 1);

        var parsed = Assert.IsType<SessionHistoryPacket>(PacketParser.Parse(packet).Packet);
        Assert.Equal(laps, parsed.Laps);
        Assert.Equal(stints, parsed.Stints);
        Assert.Equal(0, parsed.StintIndexForLap(1));
        Assert.Equal(0, parsed.StintIndexForLap(2));
        Assert.Equal(1, parsed.StintIndexForLap(3));
        Assert.Equal(61_500u, parsed.Laps[1].Sector1Ms); // minutes part recombined
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void Final_classification_round_trips(GameFormat format)
    {
        var layout = FormatLayout.For(format);
        var writer = new PacketWriter(layout);
        var player = (byte)(layout.MaxCars - 1);
        var packet = writer.Create(PacketId.FinalClassification, 1, 0, 0, player);
        var expected = new FinalClassification(3, 57, 5, 15, 1, ResultStatus.Finished, 91_931, 5_341.517, 5);
        writer.WriteFinalClassification(packet, player, expected);
        Assert.Equal(expected, Assert.IsType<FinalClassificationPacket>(PacketParser.Parse(packet).Packet).Player);
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void Car_setups_round_trip(GameFormat format)
    {
        var layout = FormatLayout.For(format);
        var writer = new PacketWriter(layout);
        var packet = writer.Create(PacketId.CarSetups, 1, 0, 0, 5);
        var expected = new CarSetup(18, 12, 100, 25, -3.4f, -1.9f, 0.01f, 0.12f, 37, 17, 15, 8, 24, 51, 100, 55, 50,
            new Tyres<float>(24.2f, 24.3f, 28.0f, 28.1f), 6, 6.5f);
        writer.WriteCarSetup(packet, 5, expected);
        writer.WriteCarSetup(packet, layout.MaxCars - 1, expected with { FrontWing = 40 });
        writer.WriteNextFrontWingValue(packet, 19);

        var parsed = Assert.IsType<CarSetupsPacket>(PacketParser.Parse(packet).Packet);
        Assert.Equal(expected, parsed.Player);
        Assert.Equal(40, parsed.Cars[^1].FrontWing);
        Assert.True(parsed.Cars[0].IsEmpty); // a car whose setup the game does not share
        Assert.Equal(19, parsed.NextFrontWingValue);
    }

    [Fact]
    public void Setups_compare_without_fuel_load()
    {
        var setup = new CarSetup(18, 12, 100, 25, -3.4f, -1.9f, 0.01f, 0.12f, 37, 17, 15, 8, 24, 51, 100, 55, 50,
            new Tyres<float>(24.2f, 24.2f, 28.0f, 28.0f), 6, 6);
        Assert.True(setup.SameSettings(setup with { FuelLoad = 5.2f }));
        Assert.False(setup.SameSettings(setup with { RearAntiRollBar = 9 }));
        Assert.False(setup.SameSettings(setup with { TyresPressure = setup.TyresPressure with { FrontLeft = 28.4f } }));
        Assert.False(setup.IsEmpty);
    }

    [Fact]
    public void Telemetry2_is_2026_only()
    {
        Assert.False(FormatLayout.F1_25.PacketSizes.ContainsKey(PacketId.CarTelemetry2));
        var writer = new PacketWriter(FormatLayout.F1_26);
        var packet = writer.Create(PacketId.CarTelemetry2, 1, 0, 0, 23);
        var expected = new CarTelemetry2(2, true, 150, true, false, 300, true, false);
        writer.WriteCarTelemetry2(packet, 23, expected);
        Assert.Equal(expected, Assert.IsType<CarTelemetry2Packet>(PacketParser.Parse(packet).Packet).Player);
    }

    [Fact]
    public void Event_code_is_decoded()
    {
        var writer = new PacketWriter(FormatLayout.F1_26);
        var packet = writer.Create(PacketId.Event, 1, 0, 0, 0);
        writer.WriteEventCode(packet, "FLBK");
        Assert.Equal("FLBK", Assert.IsType<EventPacket>(PacketParser.Parse(packet).Packet).Code);
    }
}
