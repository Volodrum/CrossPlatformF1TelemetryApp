using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace F1Telemetry.App.Controls;

/// <summary>
/// An ⓘ that explains a page or a panel in a tooltip, so instructions don't take the page's space. Hidden without text.
/// Template: Theme/Controls.axaml.
/// </summary>
public sealed class HelpTip : TemplatedControl
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<HelpTip, string?>(nameof(Text));

    static HelpTip()
    {
        ToolTip.ShowDelayProperty.OverrideDefaultValue<HelpTip>(150);
        IsVisibleProperty.OverrideDefaultValue<HelpTip>(false);
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty)
        {
            var text = change.GetNewValue<string?>();
            IsVisible = !string.IsNullOrEmpty(text);
            ToolTip.SetTip(this, IsVisible ? new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 440 } : null);
            AutomationProperties.SetHelpText(this, text);
        }
    }
}

/// <summary>
/// The top of every page: a context line with an optional <see cref="HelpTip"/>, the title, and on the right the page's
/// own tools (its <see cref="ContentControl.Content"/>: unit switches and the like). Template: Theme/Controls.axaml.
/// </summary>
public sealed class PageHeader : ContentControl
{
    public static readonly StyledProperty<string?> EyebrowProperty =
        AvaloniaProperty.Register<PageHeader, string?>(nameof(Eyebrow));

    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<PageHeader, string?>(nameof(Title));

    public static readonly StyledProperty<string?> HelpProperty =
        AvaloniaProperty.Register<PageHeader, string?>(nameof(Help));

    public string? Eyebrow
    {
        get => GetValue(EyebrowProperty);
        set => SetValue(EyebrowProperty, value);
    }

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Help
    {
        get => GetValue(HelpProperty);
        set => SetValue(HelpProperty, value);
    }
}

/// <summary>
/// The one A-versus-B picker of every comparison (race vs race, lap vs lap): side A, side B, then extra options.
/// Each side has a lettered tag in its trace colour, a label and its pickers. Template: Theme/Controls.axaml.
/// </summary>
public sealed class CompareBar : TemplatedControl
{
    public static readonly StyledProperty<string?> ALabelProperty =
        AvaloniaProperty.Register<CompareBar, string?>(nameof(ALabel));

    public static readonly StyledProperty<string?> BLabelProperty =
        AvaloniaProperty.Register<CompareBar, string?>(nameof(BLabel));

    public static readonly StyledProperty<IBrush?> ABrushProperty =
        AvaloniaProperty.Register<CompareBar, IBrush?>(nameof(ABrush));

    public static readonly StyledProperty<IBrush?> BBrushProperty =
        AvaloniaProperty.Register<CompareBar, IBrush?>(nameof(BBrush));

    public static readonly StyledProperty<object?> AContentProperty =
        AvaloniaProperty.Register<CompareBar, object?>(nameof(AContent));

    public static readonly StyledProperty<object?> BContentProperty =
        AvaloniaProperty.Register<CompareBar, object?>(nameof(BContent));

    public static readonly StyledProperty<object?> ExtraProperty =
        AvaloniaProperty.Register<CompareBar, object?>(nameof(Extra));

    public string? ALabel
    {
        get => GetValue(ALabelProperty);
        set => SetValue(ALabelProperty, value);
    }

    public string? BLabel
    {
        get => GetValue(BLabelProperty);
        set => SetValue(BLabelProperty, value);
    }

    public IBrush? ABrush
    {
        get => GetValue(ABrushProperty);
        set => SetValue(ABrushProperty, value);
    }

    public IBrush? BBrush
    {
        get => GetValue(BBrushProperty);
        set => SetValue(BBrushProperty, value);
    }

    public object? AContent
    {
        get => GetValue(AContentProperty);
        set => SetValue(AContentProperty, value);
    }

    public object? BContent
    {
        get => GetValue(BContentProperty);
        set => SetValue(BContentProperty, value);
    }

    public object? Extra
    {
        get => GetValue(ExtraProperty);
        set => SetValue(ExtraProperty, value);
    }

    /// <summary>The pickers are this control's logical children, so they inherit its DataContext like any content.</summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == AContentProperty || change.Property == BContentProperty || change.Property == ExtraProperty)
        {
            if (change.OldValue is Control old)
            {
                LogicalChildren.Remove(old);
            }

            if (change.NewValue is Control added)
            {
                LogicalChildren.Add(added);
            }
        }
    }
}
