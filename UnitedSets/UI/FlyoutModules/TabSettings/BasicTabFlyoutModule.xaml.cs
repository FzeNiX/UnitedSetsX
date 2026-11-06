using UnitedSets.Helpers;
using UnitedSets.Tabs;

namespace UnitedSets.UI.FlyoutModules;

public sealed partial class BasicTabFlyoutModule
{
    public BasicTabFlyoutModule(TabBase TabBase)
    {
        this.TabBase = TabBase;
        InitializeComponent();
        BuildColorSwatches();
    }
    readonly TabBase TabBase;

    /// <summary>
    /// Automatic, no color, and the colors of <see cref="TabColors.Palette"/>
    /// </summary>
    void BuildColorSwatches()
    {
        ColorSwatches.Children.Clear();
        var chosen = TabBase.IsTabColorChosen;
        var current = TabBase.TabColor;
        ColorSwatches.Children.Add(Swatch(
            new TextBlock { Text = "Auto", FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            null, 40, "Automatic: the color of the window's title bar (e.g. the Chrome profile color)",
            !chosen, () => TabBase.ChooseTabColor(null, automatic: true)));
        ColorSwatches.Children.Add(Swatch(
            new FontIcon { Glyph = "", FontSize = 10 },
            null, 22, "No color",
            chosen && current is null, () => TabBase.ChooseTabColor(null)));
        foreach (var (name, color) in TabColors.Palette)
            ColorSwatches.Children.Add(Swatch(null, color, 22, name,
                chosen && current is { } c && c.Equals(color), () => TabBase.ChooseTabColor(color)));
        ColorScopeText.Text = TabBase.ColorIdentity is { } id
            ? $"Applies to {TabColors.DescribeIdentity(id)}."
            : "Applies to this tab.";
    }

    Border Swatch(UIElement? content, Color? color, double width, string tooltip, bool selected, Action choose)
    {
        var resources = Application.Current.Resources;
        var swatch = new Border
        {
            Width = width,
            Height = 22,
            CornerRadius = new(11),
            Child = content,
            Background = color is { } c ? new SolidColorBrush(c) : (Brush)resources["ControlFillColorDefaultBrush"],
            BorderThickness = new(selected ? 2 : 1),
            BorderBrush = selected
                ? (Brush)resources["TextFillColorPrimaryBrush"]
                : (Brush)resources["ControlStrokeColorDefaultBrush"]
        };
        ToolTipService.SetToolTip(swatch, tooltip);
        swatch.Tapped += (_, e) =>
        {
            e.Handled = true;
            choose();
            BuildColorSwatches();
        };
        return swatch;
    }

    [Event(typeof(TextChangedEventHandler))]
    private void TabNameTextBoxChanged()
    {
        TabBase.CustomTitle = TabNameTextBox.Text;
    }

    [Event(typeof(RoutedEventHandler))]
    private void TabNameReset()
    {
        TabNameTextBox.Text = "";
    }
}
