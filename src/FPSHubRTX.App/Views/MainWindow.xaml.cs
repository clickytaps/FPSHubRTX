using System.ComponentModel;
using System.Windows;
using System.Windows.Media.Animation;
using FPSHubRTX.App.Services;
using FPSHubRTX.ViewModels;

namespace FPSHubRTX.App.Views;

public partial class MainWindow : Window
{
    public static readonly DependencyProperty IsNarrowProperty = DependencyProperty.Register(nameof(IsNarrow), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));

    private MainViewModel? _vm;
    private AppHost? _host;

    public MainWindow()
    {
        InitializeComponent();
        StateChanged += (_, _) => OnStateChanged();
        SizeChanged += (_, _) => UpdateResponsiveLayout();
    }

    /// <summary>Collapses the sidebar to icons on narrow windows.</summary>
    public bool IsNarrow
    {
        get => (bool)GetValue(IsNarrowProperty);
        set => SetValue(IsNarrowProperty, value);
    }

    public void Attach(MainViewModel vm, AppHost host)
    {
        _vm = vm;
        _host = host;
        vm.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.CurrentPage) || !ThemeService.AnimationsEnabled) return;
        // Subtle fade + slide when switching pages.
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        var slide = new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        PageHost.BeginAnimation(OpacityProperty, fade);
        PageShift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, slide);
    }

    private void UpdateResponsiveLayout()
    {
        IsNarrow = ActualWidth < 1040;
        SidebarColumn.Width = new GridLength(IsNarrow ? 76 : 236);
        BrandText.Visibility = IsNarrow ? Visibility.Collapsed : Visibility.Visible;
        StatusCard.Visibility = IsNarrow ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnStateChanged()
    {
        // WindowChrome windows overhang the screen by the resize border when maximised.
        RootBorder.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";
        MaxButton.ToolTip = WindowState == WindowState.Maximized ? "Restore" : "Maximise";

        if (WindowState == WindowState.Minimized && _host?.S.MinimizeToTray == true)
        {
            Hide(); // the tray icon brings it back
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        if (((App)Application.Current).IsExiting)
        {
            base.OnClosing(e);
            return;
        }
        if (_host?.S.CloseToTray == true)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        e.Cancel = true; // shut down through App so sessions are saved and PresentMon is stopped
        ((App)Application.Current).ExitApplication();
    }
}
