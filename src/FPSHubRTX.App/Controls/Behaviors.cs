using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace FPSHubRTX.App.Controls;

/// <summary>Attached behaviour: opening a button's ContextMenu with a normal left click (the card "⋯" menu).</summary>
public static class MenuButton
{
    public static readonly DependencyProperty OpenOnClickProperty = DependencyProperty.RegisterAttached(
        "OpenOnClick", typeof(bool), typeof(MenuButton), new PropertyMetadata(false, OnChanged));

    public static bool GetOpenOnClick(DependencyObject d) => (bool)d.GetValue(OpenOnClickProperty);
    public static void SetOpenOnClick(DependencyObject d, bool value) => d.SetValue(OpenOnClickProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ButtonBase button) return;
        button.Click -= OnClick;
        if (e.NewValue is true) button.Click += OnClick;
    }

    private static void OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.ContextMenu is null) return;
        fe.ContextMenu.DataContext = fe.DataContext;
        fe.ContextMenu.PlacementTarget = fe;
        fe.ContextMenu.Placement = PlacementMode.Bottom;
        fe.ContextMenu.IsOpen = true;
    }
}
