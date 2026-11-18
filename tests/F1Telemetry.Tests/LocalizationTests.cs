using System.Text.RegularExpressions;
using F1Telemetry.Core.Localization;

namespace F1Telemetry.Tests;

/// <summary><see cref="Loc"/> is app-wide state: these tests switch it, so they never run alongside other tests.</summary>
[CollectionDefinition(nameof(LocalizationTests), DisableParallelization = true)]
public sealed class LocalizationCollection;

[Collection(nameof(LocalizationTests))]
public sealed partial class LocalizationTests
{
    private static void InUkrainian(Action test)
    {
        try
        {
            Loc.Use(Loc.Ukrainian);
            test();
        }
        finally
        {
            Loc.Use(Loc.English);
        }
    }

    [Fact]
    public void English_leaves_every_text_alone()
    {
        Loc.Use(Loc.English);

        Assert.Equal("Start / stop recording", Loc.T("Start / stop recording"));
        Assert.Equal("12 laps", Loc.T("12 laps"));
    }

    [Fact]
    public void Ukrainian_translates_whole_texts_and_keeps_unknown_ones() => InUkrainian(() =>
    {
        Assert.Equal("Почати / зупинити запис", Loc.T("Start / stop recording"));
        Assert.Equal("Monza", Loc.T("Monza"));
        Assert.Equal("1:23.456", Loc.T("1:23.456"));
    });

    [Theory]
    [InlineData("1 lap", "1 коло")]
    [InlineData("3 laps", "3 кола")]
    [InlineData("5 laps", "5 кіл")]
    [InlineData("11 laps", "11 кіл")]
    [InlineData("21 laps", "21 коло")]
    [InlineData("24 laps", "24 кола")]
    public void Counts_agree_with_their_number(string english, string ukrainian) => InUkrainian(() => Assert.Equal(ukrainian, Loc.T(english)));

    [Fact]
    public void Labels_built_of_parts_are_translated_part_by_part() => InUkrainian(() =>
    {
        Assert.Equal("Monza · Гонка · 12 кіл", Loc.T("Monza · Race · 12 laps"));
        Assert.Equal("Обидві гонки пройшли 12 кіл. Вплив пального 0.027 с/кг, типове значення F1.",
            Loc.T("After 12 laps both races completed. Fuel effect 0.027 s/kg, typical F1 value."));
    });

    [Fact]
    public void Capitals_find_their_key_in_any_case() => InUkrainian(() =>
    {
        Assert.Equal("ПЛАН ГОНКИ", Loc.T("RACE PLAN"));
        Assert.Equal("МОДЕЛЬ БОЛІДА · MONZA · F1 26 · ВИВЧЕНО ЗА 30 КОЛАМИ", Loc.T("CAR MODEL · MONZA · F1 26 · LEARNED FROM 30 LAPS"));
    });

    [Fact]
    public void Spacing_around_a_text_is_kept() => InUkrainian(() => Assert.Equal(" · макс. ", Loc.T(" · top ")));

    [Fact]
    public void Plural_forms_follow_the_Ukrainian_rule()
    {
        Assert.Equal(0, Loc.UkrainianForm(1));
        Assert.Equal(0, Loc.UkrainianForm(101));
        Assert.Equal(1, Loc.UkrainianForm(2));
        Assert.Equal(1, Loc.UkrainianForm(34));
        Assert.Equal(2, Loc.UkrainianForm(11));
        Assert.Equal(2, Loc.UkrainianForm(14));
        Assert.Equal(2, Loc.UkrainianForm(0));
        Assert.Equal(1, Loc.UkrainianForm(2.5));
    }

    [Fact]
    public void Every_translation_fills_only_holes_its_key_has()
    {
        var table = typeof(Loc).GetMethod("Load", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, [Loc.Ukrainian]) as Dictionary<string, string>;

        Assert.NotNull(table);
        Assert.True(table.Count > 500, $"only {table.Count} Ukrainian texts loaded");
        foreach (var (key, value) in table)
        {
            var holes = Hole().Matches(key).Select(m => m.Groups[1].Value).ToHashSet();
            foreach (Match slot in Hole().Matches(value))
            {
                Assert.True(holes.Contains(slot.Groups[1].Value), $"\"{value}\" fills hole {slot.Groups[1].Value}, which \"{key}\" doesn't have");
            }
        }
    }

    [GeneratedRegex(@"\{(\d+)(?:\|[^{}]*)?\}")]
    private static partial Regex Hole();
}
