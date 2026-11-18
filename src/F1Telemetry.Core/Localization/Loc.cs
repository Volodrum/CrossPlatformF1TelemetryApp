using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace F1Telemetry.Core.Localization;

/// <summary>A language the app can be shown in.</summary>
/// <param name="Code">Two-letter code, as saved in the settings ("en", "uk").</param>
/// <param name="Name">Its own name for itself ("English", "Українська").</param>
public sealed record AppLanguage(string Code, string Name);

/// <summary>
/// The app's translations, keyed by the English text itself: whatever has no translation stays English.
/// <para>
/// A key is either a whole text ("Start / stop recording") or a template with numbered holes
/// ("{0} clean laps, longest run {1}") that matches texts built from it; what fills each hole is translated too, so
/// "{0} long run" with "Soft" in it comes out with the soft compound's own translation. A translation can agree with a
/// number in a hole: <c>{0|коло|кола|кіл}</c> picks the Ukrainian form for one, a few, or many of what is in hole 0.
/// </para>
/// The tables are embedded JSON files, <c>Localization/{code}.json</c>, one object of English → translation.
/// </summary>
public static partial class Loc
{
    public const string English = "en";
    public const string Ukrainian = "uk";

    public static IReadOnlyList<AppLanguage> Languages { get; } = [new(English, "English"), new(Ukrainian, "Українська")];

    // A cap on remembered results: live texts (timers, gaps) are new every frame and never repeat.
    private const int CacheLimit = 20_000;

    private static Dictionary<string, string> _exact = [];
    private static Dictionary<string, string> _capitals = [];
    private static Template[] _templates = [];

    /// <summary>The culture of <see cref="Language"/>, for its capital letters.</summary>
    private static CultureInfo Culture { get; set; } = CultureInfo.InvariantCulture;
    private static ConcurrentDictionary<string, string> _cache = new(StringComparer.Ordinal);

    /// <summary>The language in use ("en" until <see cref="Use"/> picks another).</summary>
    public static string Language { get; private set; } = English;

    public static bool IsEnglish => Language == English;

    /// <summary>Raised once per text shown with words in it and no translation: for finding what is left to translate.</summary>
    public static event Action<string>? Missing;

    /// <summary>The language for a saved setting: the setting when it is one we have, else the system's when we have it, else English.</summary>
    public static string Resolve(string? saved)
    {
        if (Languages.Any(l => l.Code == saved))
        {
            return saved!;
        }

        var system = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return Languages.Any(l => l.Code == system) ? system : English;
    }

    /// <summary>Shows the app in <paramref name="code"/> from now on (texts already on screen keep their language).</summary>
    public static void Use(string? code)
    {
        var language = Languages.Any(l => l.Code == code) ? code! : English;
        var table = language == English ? [] : Load(language);
        Use(language, table);
    }

    /// <summary>Uses <paramref name="table"/> (English → translation) for <paramref name="language"/>; for tests.</summary>
    public static void Use(string language, IReadOnlyDictionary<string, string> table)
    {
        var exact = new Dictionary<string, string>(StringComparer.Ordinal);
        var templates = new List<Template>();
        foreach (var (key, value) in table)
        {
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(value))
            {
                continue;
            }

            if (Hole().IsMatch(key))
            {
                templates.Add(Template.Create(key, value));
            }
            else
            {
                exact[key] = value;
            }
        }

        // Every key in capitals, for labels shown in capitals; a key written in capitals itself wins.
        var capitals = new Dictionary<string, string>(StringComparer.Ordinal);
        var culture = CultureInfo.GetCultureInfo(language);
        foreach (var (key, value) in exact)
        {
            capitals.TryAdd(key.ToUpperInvariant(), value.ToUpper(culture));
        }

        foreach (var (key, value) in exact.Where(e => e.Key == e.Key.ToUpperInvariant()))
        {
            capitals[key] = value;
        }

        // Longer keys first: "{0} clean laps here" before "{0} clean laps".
        _templates = [.. templates.OrderByDescending(t => t.LiteralLength)];
        _capitals = capitals;
        Culture = culture;
        _exact = exact;
        _cache = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        Language = language;
    }

    /// <summary>The translation of <paramref name="text"/>, or the text itself when there is none.</summary>
    public static string T(string text) => Translate(text, 0);

    /// <summary><see cref="T(string)"/> for texts that may be null.</summary>
    public static string? TOrNull(string? text) => text is null ? null : Translate(text, 0);

    /// <summary>
    /// Formats <paramref name="format"/> (an English key with numbered holes) with <paramref name="args"/> in the
    /// invariant culture, then translates the result.
    /// </summary>
    public static string F(string format, params object?[] args) => T(string.Format(CultureInfo.InvariantCulture, format, args));

    private static string Translate(string text, int depth)
    {
        if (Language == English || text.Length < 2 || depth > 8 || !HasWord(text))
        {
            return text;
        }

        if (_exact.TryGetValue(text, out var exact))
        {
            return exact;
        }

        if (_cache.TryGetValue(text, out var cached))
        {
            return cached;
        }

        var result = Lookup(text, depth);
        if (depth == 0 && ReferenceEquals(result, text))
        {
            Missing?.Invoke(text);
        }

        if (_cache.Count >= CacheLimit)
        {
            _cache.Clear();
        }

        _cache[text] = result;
        return result;
    }

    private static string Lookup(string text, int depth)
    {
        // Spacing around a text (" · top ") isn't part of its key.
        var trimmed = text.Trim();
        if (trimmed.Length != text.Length && trimmed.Length > 0)
        {
            var inner = Translate(trimmed, depth);
            if (!ReferenceEquals(inner, trimmed) && inner != trimmed)
            {
                var start = text.IndexOf(trimmed, StringComparison.Ordinal);
                return string.Concat(text.AsSpan(0, start), inner, text.AsSpan(start + trimmed.Length));
            }
        }

        foreach (var template in _templates)
        {
            if (template.TryApply(text, depth, ignoreCase: false, out var applied))
            {
                return applied;
            }
        }

        // Labels are often a text in capitals ("CAR MODEL · …"): the key in any case, the translation in capitals.
        if (IsCapitals(text))
        {
            if (_capitals.TryGetValue(text, out var capitals))
            {
                return capitals;
            }

            foreach (var template in _templates)
            {
                if (template.TryApply(text, depth, ignoreCase: true, out var applied))
                {
                    return applied.ToUpper(Culture);
                }
            }
        }

        return text;
    }

    /// <summary>Has letters, and none of them lower case.</summary>
    private static bool IsCapitals(string text)
    {
        var letters = false;
        foreach (var c in text)
        {
            if (char.IsLower(c))
            {
                return false;
            }

            letters |= char.IsLetter(c);
        }

        return letters;
    }

    /// <summary>A text worth looking up has a word in it (two letters in a row); "1:23.456" and "+0.12 s" don't.</summary>
    private static bool HasWord(string text)
    {
        var run = 0;
        foreach (var c in text)
        {
            run = char.IsLetter(c) ? run + 1 : 0;
            if (run >= 2)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Every embedded table of the language (<c>uk.json</c>, <c>uk.energy.json</c>, …) merged into one.</summary>
    private static Dictionary<string, string> Load(string language)
    {
        var assembly = typeof(Loc).Assembly;
        var options = new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        var table = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in assembly.GetManifestResourceNames()
                     .Where(n => n.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                                 && (n.EndsWith($"Localization.{language}.json", StringComparison.OrdinalIgnoreCase)
                                     || n.Contains($"Localization.{language}.", StringComparison.OrdinalIgnoreCase)))
                     .Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            foreach (var (key, value) in JsonSerializer.Deserialize<Dictionary<string, string>>(stream, options) ?? [])
            {
                table[key] = value;
            }
        }

        return table;
    }

    /// <summary>The Ukrainian form for <paramref name="count"/>: 0 for one (1, 21), 1 for a few (2–4, 22–24), 2 for many (5–20, 25…); 1 for fractions.</summary>
    public static int UkrainianForm(double count)
    {
        if (Math.Abs(count - Math.Round(count)) > 1e-9)
        {
            return 1;
        }

        var n = (long)Math.Abs(Math.Round(count));
        return (n % 10, n % 100) switch
        {
            (1, not 11) => 0,
            ( >= 2 and <= 4, not (>= 12 and <= 14)) => 1,
            _ => 2,
        };
    }

    [GeneratedRegex(@"\{\d+\}")]
    private static partial Regex Hole();

    [GeneratedRegex(@"\{(\d+)(?:\|([^{}]*))?\}")]
    private static partial Regex Slot();

    [GeneratedRegex(@"[-+−]?\d+(?:[.,]\d+)?")]
    private static partial Regex Number();

    /// <summary>A key with holes: the English pattern it matches, and the translation to fill in.</summary>
    private sealed class Template
    {
        private readonly Regex _pattern;
        private readonly string _translation;
        private readonly string _anchor;
        private Regex? _anyCase;

        private Template(Regex pattern, string translation, string anchor, int literalLength)
        {
            _pattern = pattern;
            _translation = translation;
            _anchor = anchor;
            LiteralLength = literalLength;
        }

        public int LiteralLength { get; }

        public static Template Create(string key, string translation)
        {
            var pattern = new StringBuilder("^");
            var literals = new List<string>();
            var at = 0;
            foreach (Match hole in Hole().Matches(key))
            {
                var literal = key[at..hole.Index];
                literals.Add(literal);
                pattern.Append(Regex.Escape(literal));
                pattern.Append("(?<h").Append(hole.Value[1..^1]).Append(">.+?)");
                at = hole.Index + hole.Length;
            }

            literals.Add(key[at..]);
            pattern.Append(Regex.Escape(key[at..])).Append('$');
            var anchor = literals.MaxBy(l => l.Length) ?? "";
            return new Template(new Regex(pattern.ToString(), RegexOptions.Singleline | RegexOptions.CultureInvariant), translation, anchor,
                literals.Sum(l => l.Length));
        }

        public bool TryApply(string text, int depth, bool ignoreCase, out string result)
        {
            result = text;
            if (_anchor.Length > 0 && !text.Contains(_anchor, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                return false;
            }

            var pattern = !ignoreCase ? _pattern
                : _anyCase ??= new Regex(_pattern.ToString(), RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
            var match = pattern.Match(text);
            if (!match.Success)
            {
                return false;
            }

            result = Slot().Replace(_translation, slot =>
            {
                var group = match.Groups["h" + slot.Groups[1].Value];
                if (!group.Success)
                {
                    return slot.Value;
                }

                // {0|one|few|many}: the word that agrees with the number in hole 0.
                if (slot.Groups[2].Success)
                {
                    var forms = slot.Groups[2].Value.Split('|');
                    // The number next to the word: the last one in the hole ("Monza · 12" agrees with 12).
                    var number = Number().Matches(group.Value).LastOrDefault();
                    var count = number is { Success: true } && double.TryParse(number.Value.Replace(',', '.').Replace('−', '-'), NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
                        ? n
                        : 5;
                    return forms[Math.Min(UkrainianForm(count), forms.Length - 1)];
                }

                return Translate(group.Value, depth + 1);
            });
            return true;
        }
    }
}
