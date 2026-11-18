using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Data;
using F1Telemetry.Core.Localization;

namespace F1Telemetry.App.Infrastructure;

/// <summary>
/// Shows every text on screen in the app's language: each time a <see cref="TextBlock"/> or <see cref="Run"/> gets a text
/// (from markup, a binding, a style, a button's content, a tooltip), its translation (see <see cref="Loc"/>) replaces it
/// as the current value. The text's own source stays in place, so a binding that changes later is translated again.
/// Text boxes are left alone: what the user types is theirs.
/// </summary>
public static class UiTranslation
{
    private static bool _installed;

    public static void Install()
    {
        if (_installed)
        {
            return;
        }

        _installed = true;
        TextBlock.TextProperty.Changed.AddClassHandler<TextBlock>((block, e) => Translate(block, TextBlock.TextProperty, e));
        Run.TextProperty.Changed.AddClassHandler<Run>((run, e) => Translate(run, Run.TextProperty, e));
        Window.TitleProperty.Changed.AddClassHandler<Window>((window, e) => Translate(window, Window.TitleProperty, e));
    }

    private static void Translate(AvaloniaObject target, StyledProperty<string?> property, AvaloniaPropertyChangedEventArgs e)
    {
        if (Loc.IsEnglish || e.NewValue is not string text || e.Priority == BindingPriority.Animation)
        {
            return;
        }

        var translated = Loc.T(text);
        if (!ReferenceEquals(translated, text) && translated != text)
        {
            target.SetCurrentValue(property, translated);
        }
    }
}
