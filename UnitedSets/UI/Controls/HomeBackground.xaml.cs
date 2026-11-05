namespace UnitedSets.UI.Controls;

public partial class HomeBackground : StackPanel
{
	public HomeBackground()
	{
		InitializeComponent();
        var homePageSetting = UnitedSetsApp.Current.Settings.HomePageInfo;
        void Update()
        {
            hintInfo.Visibility = homePageSetting.Value ? Visibility.Visible : Visibility.Collapsed;
            descInfo.Visibility = homePageSetting.Value ? Visibility.Collapsed : Visibility.Visible;
        }
        PropertyChangedEventHandler handler = (_, _) =>
        {
            try { Update(); }
            catch (Exception ex) { Helpers.CrashLog.Write("Home page setting", ex); }
        };
        // only while this element is in a window (the setting outlives the set windows)
        Loaded += (_, _) => { homePageSetting.PropertyChanged -= handler; homePageSetting.PropertyChanged += handler; Update(); };
        Unloaded += (_, _) => homePageSetting.PropertyChanged -= handler;
        Update();
    }
}
