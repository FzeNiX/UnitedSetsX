using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Controls.Primitives;
using UnitedSets.Tabs;

namespace UnitedSets.UI.Controls;

/// <summary>
/// Shows the color of a tab (<see cref="TabBase.TabColor"/>) on its <see cref="TabViewItem"/>: the whole tab is
/// tinted, more strongly when it is selected, with a solid line at the bottom of the tabs that are not selected
/// (like Chrome's tab groups).
/// </summary>
/// <remarks>
/// The standard tab template only uses theme brushes for the selected/pointer over states, so a layer is added to the
/// template, between the tab background and its content (the hover/pressed effects still show above it).
/// </remarks>
public static class TabColorLayer
{
    sealed class State
    {
        public required TabViewItem Item;
        public required Border Fill;
        public required Border Line;
        public TabBase? Tab;
        public long SelectedToken;
        public PropertyChangedEventHandler? Handler;
    }
    static readonly ConditionalWeakTable<TabViewItem, State> states = [];

    /// <summary>
    /// Call when the <see cref="TabViewItem"/> is loaded (its template is applied)
    /// </summary>
    public static void Attach(TabViewItem item)
    {
        if (!states.TryGetValue(item, out var state))
        {
            if (VisualTreeHelper.GetChildrenCount(item) is 0 || VisualTreeHelper.GetChild(item, 0) is not Grid layoutRoot)
                return;
            // between the selected tab background (and the other parts of the template) and the content
            var index = layoutRoot.Children.IndexOf(layoutRoot.Children.OfType<FrameworkElement>().FirstOrDefault(x => x.Name is "TabContainer"));
            if (index < 0) index = layoutRoot.Children.Count;
            var radius = item.CornerRadius;
            var fill = new Border
            {
                IsHitTestVisible = false,
                CornerRadius = new(radius.TopLeft, radius.TopRight, 0, 0),
                Visibility = Visibility.Collapsed
            };
            var line = new Border
            {
                IsHitTestVisible = false,
                Height = 2,
                Margin = new(4, 0, 4, 0),
                CornerRadius = new(1),
                VerticalAlignment = VerticalAlignment.Bottom,
                Visibility = Visibility.Collapsed
            };
            Grid.SetColumnSpan(fill, 3);
            Grid.SetColumnSpan(line, 3);
            layoutRoot.Children.Insert(index, line);
            layoutRoot.Children.Insert(index, fill);
            state = new() { Item = item, Fill = fill, Line = line };
            var s = state;
            state.Handler = (_, e) =>
            {
                if (e.PropertyName is nameof(TabBase.TabColor) or null)
                    Update(s);
            };
            states.Add(item, state);
            item.ActualThemeChanged += (_, _) => Update(state);
            item.DataContextChanged += (_, _) => Bind(state);
            item.Unloaded += (_, _) => Unbind(state);
            item.Loaded += (_, _) => Bind(state);
        }
        if (state.SelectedToken == 0)
            state.SelectedToken = item.RegisterPropertyChangedCallback(SelectorItem.IsSelectedProperty, (_, _) => Update(state));
        Bind(state);
    }

    static void Bind(State state)
    {
        var tab = state.Item.DataContext as TabBase ?? state.Item.Tag as TabBase;
        if (tab != state.Tab)
        {
            if (state.Tab is not null) state.Tab.PropertyChanged -= state.Handler;
            state.Tab = tab;
            if (tab is not null) tab.PropertyChanged += state.Handler;
        }
        Update(state);
    }

    static void Unbind(State state)
    {
        if (state.Tab is not null) state.Tab.PropertyChanged -= state.Handler;
        state.Tab = null;
    }

    static void Update(State state)
    {
        var color = state.Tab?.TabColor;
        if (color is not { } c)
        {
            state.Fill.Visibility = state.Line.Visibility = Visibility.Collapsed;
            return;
        }
        var dark = state.Item.ActualTheme is ElementTheme.Dark;
        var selected = state.Item.IsSelected;
        byte alpha = selected ? (byte)(dark ? 0x8C : 0x80) : (byte)(dark ? 0x4D : 0x40);
        state.Fill.Background = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
        state.Fill.Visibility = Visibility.Visible;
        state.Line.Background = new SolidColorBrush(c);
        state.Line.Visibility = selected ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// For lists (vertical tabs): a brush of the tab color, or transparent
    /// </summary>
    public static Brush ToBrush(Color? color)
        => new SolidColorBrush(color ?? Colors.Transparent);
}
