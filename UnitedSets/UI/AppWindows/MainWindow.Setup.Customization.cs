using System.IO;
using UnitedSets.Apps;
using UnitedSets.Configurations;
using UnitedSets.Mvvm.Services;
using UnitedSets.Settings;
using WinUIEx;

namespace UnitedSets.UI.AppWindows;

partial class MainWindow
{
    void SetupCustomization()
    {
        UnitedSetsApp.Current.Configuration.PersistantService.FinalizeLoadAsync();

        void SetupSetting<T>(Setting<T> setting, Action<T> onUpdate)
        {
            // The settings live as long as the app, this window doesn't: unsubscribe when it closes.
            // (Changing a setting after a set window was closed used to update the closed window, e.g. set its
            // SystemBackdrop, which crashed the whole app.)
            PropertyChangedEventHandler handler = (_, _) =>
            {
                if (IsClosed) return;
                try { onUpdate(setting.Value); }
                catch (Exception ex) { Helpers.CrashLog.Write($"Applying setting {setting.Title}", ex); }
            };
            setting.PropertyChanged += handler;
            Closed += (_, _) => setting.PropertyChanged -= handler;
            onUpdate(setting.Value);
        }
        void SetupStartupSetting<T>(Setting<T> setting, Action<T> onUpdate)
        {
            onUpdate(setting.Value);
        }
        var settings = UnitedSetsApp.Current.Settings;
        //var gradStops = (WindowBorderOnTransparent.BorderBrush as LinearGradientBrush)!.GradientStops;
        SetupSetting(settings.TaskbarIcon, x =>
        {
            if (x is not null)
            {
                var y = Path.IsPathRooted(x) ? x : Path.Combine(USConfig.RootLocation, x);
                if (File.Exists(y))
                {
                    var icon = Icon.FromFile(y);
                    this.SetTaskBarIcon(icon);
                }
                else
                    // a missing icon must not prevent the set window from opening (it used to throw here)
                    Helpers.CrashLog.Write("Taskbar icon", new FileNotFoundException($"File not found: {y}"));
            }
        });
        SetupSetting(settings.BackdropMode, x => SystemBackdrop = x.GetSystemBackdrop());
        SetupSetting(settings.BypassMinimumSize, bypass => {
            MinWidth = bypass ? Constants.BypassMinWidth : Constants.MinWidth;
            MinHeight = bypass ? Constants.BypassMinHeight : Constants.MinHeight;
        });
        SetupSetting(settings.Background, x => MainBackgroundColor.Color = x);
        SetupSetting(settings.BorderColor, x => MainBorderColor.Color = x);
        //SetupSetting(settings.BorderGradient1, x => gradStops[^1].Color = x);
        SetupSetting(settings.BorderThickness, x => RootGrid.BorderThickness = x);
        //SetupSetting(settings.MainMargin, x => WindowBorderOnTransparent.Margin = x);
        SetupSetting(settings.CornerRadius, x => RootGrid.CornerRadius = x);
        SetupSetting(settings.WindowTitlePrefix, x => UpdateTitle());
        SetupStartupSetting(settings.InitialWindowSize, size =>
        {
            if (size.HasValue) this.SetWindowSize(size.Value.Width, size.Value.Height);
        });

    }
}
