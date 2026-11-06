namespace UnitedSets.Tabs;

partial class WindowHostTab
{
    public override void UpdateStatusLoop()
    {
        if (_Title != DefaultTitle)
        {
            _Title = DefaultTitle;
            UIDispatcher?.TryEnqueue(() => InvokePropertyChanged(nameof(DefaultTitle)));
            if (!string.IsNullOrWhiteSpace(CustomTitle))
                UIDispatcher?.TryEnqueue(() => TitleChanged());
        }
        // the automatic color: look again every few seconds while the window is shown (its theme may change),
        // and once for windows that were hidden from the start
        if (++colorCheckTicks >= 6 && (!colorDetected || Window.IsVisible))
        {
            colorCheckTicks = 0;
            DetectColor();
        }
        var icon = Window.LargeIcon;
        if (icon == default) icon = Window.SmallIcon;
        if (_Icon != icon)
        {
            _Icon = icon;
            UIDispatcher?.TryEnqueue(UpdateAppIcon);
        }
    }
}
