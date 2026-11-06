using UnitedSets.Helpers;

namespace UnitedSets.Tabs;

partial class TabBase
{
    /// <summary>
    /// What a color chosen for this tab applies to (see <see cref="TabColors.GetIdentity"/>), or <c>null</c> if the
    /// choice only applies to this tab.
    /// </summary>
    public virtual string? ColorIdentity => null;

    /// <summary>
    /// The color of the window's title bar (see <see cref="TabColors.TryDetectTitleBarColor"/>)
    /// </summary>
    Color? detectedColor;
    // the choice for this tab only, when it has no ColorIdentity
    bool hasOwnColorChoice;
    Color? ownColorChoice;

    /// <summary>
    /// The color of the tab, or <c>null</c> for no color
    /// </summary>
    public Color? TabColor
    {
        get
        {
            if (ColorIdentity is { } id && TabColors.TryGetChosen(id, out var chosen)) return chosen;
            if (hasOwnColorChoice) return ownColorChoice;
            return Settings.AutoTabColors.Value && detectedColor is { } c ? TabColors.ToTabColor(c) : null;
        }
    }

    /// <summary>
    /// Whether the user picked the color (instead of automatic)
    /// </summary>
    public bool IsTabColorChosen => ColorIdentity is { } id ? TabColors.TryGetChosen(id, out _) : hasOwnColorChoice;

    /// <summary>
    /// Picks the color of the tab (and of the other windows of the same app/profile, see <see cref="ColorIdentity"/>).
    /// </summary>
    /// <param name="color">The color, or <c>null</c> for no color</param>
    /// <param name="automatic">Go back to the automatic color instead</param>
    public void ChooseTabColor(Color? color, bool automatic = false)
    {
        if (ColorIdentity is { } id)
        {
            TabColors.SetChosen(id, color, automatic);
            return;
        }
        hasOwnColorChoice = !automatic;
        ownColorChoice = color;
        OnTabColorChanged();
    }

    /// <summary>
    /// Sets the color of the window's title bar (can be called from any thread)
    /// </summary>
    protected void SetDetectedColor(Color? color)
    {
        if (Nullable.Equals(detectedColor, color)) return;
        detectedColor = color;
        UnitedSetsApp.Current.DispatcherQueue.TryEnqueue(OnTabColorChanged);
    }

    void OnTabColorChanged() => InvokePropertyChanged(nameof(TabColor));

    /// <summary>
    /// The chosen colors or the setting changed: update every tab
    /// </summary>
    public static void RefreshAllTabColors()
    {
        UnitedSetsApp.Current.DispatcherQueue.TryEnqueue(() =>
        {
            foreach (var tab in AllTabs.ToArray())
                tab.OnTabColorChanged();
        });
    }
}
