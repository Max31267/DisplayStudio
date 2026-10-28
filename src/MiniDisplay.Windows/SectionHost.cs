using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace MiniDisplay.Windows;

// Keeps each screen's controls and draft state alive while exposing only the active screen.
public sealed class SectionHost : Grid
{
    public static readonly DependencyProperty SelectedIndexProperty = DependencyProperty.Register(
        nameof(SelectedIndex), typeof(int), typeof(SectionHost), new FrameworkPropertyMetadata(0, Changed), value => (int)value >= 0);
    public static readonly RoutedEvent SelectionChangedEvent = Selector.SelectionChangedEvent.AddOwner(typeof(SectionHost));
    public int SelectedIndex { get => (int)GetValue(SelectedIndexProperty); set => SetValue(SelectedIndexProperty, value); }
    public event SelectionChangedEventHandler SelectionChanged { add => AddHandler(SelectionChangedEvent, value); remove => RemoveHandler(SelectionChangedEvent, value); }
    protected override void OnInitialized(EventArgs e) { base.OnInitialized(e); Refresh(); }
    private static void Changed(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var host = (SectionHost)sender;
        host.Refresh();
        host.RaiseEvent(new SelectionChangedEventArgs(SelectionChangedEvent, (IList)new[] { e.OldValue }, (IList)new[] { e.NewValue }));
    }
    private void Refresh()
    {
        for (int i = 0; i < Children.Count; i++) Children[i].Visibility = i == SelectedIndex ? Visibility.Visible : Visibility.Collapsed;
    }
}
