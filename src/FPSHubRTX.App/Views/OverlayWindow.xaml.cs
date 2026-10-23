using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using FPSHubRTX.App.Services;
using FPSHubRTX.Core.Models;

namespace FPSHubRTX.App.Views;

/// <summary>
/// Always-on-top, click-through, non-activating transparent window drawn by Windows' compositor.
/// No code is injected into the game. It appears over windowed and borderless-fullscreen games;
/// exclusive-fullscreen games may cover it (see docs) — use the Performance panel or a second monitor then.
/// </summary>
public partial class OverlayWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_LAYERED = 0x80000;

    private readonly DispatcherTimer _topmostTimer;
    private OverlaySettings? _settings;
    private bool _editMode;

    public OverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => SetClickThrough(true);
        SizeChanged += (_, _) => Reposition();
        MouseLeftButtonDown += OnMouseDown;
        // Some borderless games re-assert topmost; re-assert ours periodically while visible.
        _topmostTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _topmostTimer.Tick += (_, _) =>
        {
            if (IsVisible && (_settings?.AlwaysOnTop ?? true))
            {
                Topmost = false;
                Topmost = true;
            }
        };
        IsVisibleChanged += (_, _) => { if (IsVisible) _topmostTimer.Start(); else _topmostTimer.Stop(); };
    }

    /// <summary>Raised with the new position when the user finishes dragging in edit mode.</summary>
    public event Action<double, double>? PositionSaved;

    public void ApplySettings(OverlaySettings s)
    {
        _settings = s;
        FontSize = s.FontSize;
        Topmost = s.AlwaysOnTop;
        var bg = ThemeService.Parse(s.BackgroundColor, Colors.Black);
        OverlayPanel.Background = new SolidColorBrush(Color.FromArgb((byte)Math.Round(Math.Clamp(s.BackgroundOpacity, 0, 1) * 255), bg.R, bg.G, bg.B));
        if (!s.Enabled && IsVisible) Hide();
        Reposition();
    }

    public void SetEditMode(bool edit)
    {
        _editMode = edit;
        SetClickThrough(!edit);
        OverlayPanel.BorderBrush = edit ? (Brush)FindResource("Accent2Brush") : Brushes.Transparent;
        Cursor = edit ? Cursors.SizeAll : null;
        if (!edit) PositionSaved?.Invoke(Left, Top);
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_editMode) return;
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void Reposition()
    {
        if (_settings is null || _editMode) return;
        var area = SystemParameters.WorkArea;
        const double margin = 16;
        var w = ActualWidth > 0 ? ActualWidth : 160;
        var h = ActualHeight > 0 ? ActualHeight : 80;
        switch (_settings.Corner)
        {
            case OverlayCorner.TopLeft: Left = area.Left + margin; Top = area.Top + margin; break;
            case OverlayCorner.TopRight: Left = area.Right - w - margin; Top = area.Top + margin; break;
            case OverlayCorner.BottomLeft: Left = area.Left + margin; Top = area.Bottom - h - margin; break;
            case OverlayCorner.BottomRight: Left = area.Right - w - margin; Top = area.Bottom - h - margin; break;
            default:
                // Keep a custom position on-screen even if the display layout changed.
                Left = Math.Clamp(_settings.CustomLeft, SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 40);
                Top = Math.Clamp(_settings.CustomTop, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 40);
                break;
        }
    }

    private void SetClickThrough(bool clickThrough)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var style = GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED;
        style = clickThrough ? style | WS_EX_TRANSPARENT : style & ~WS_EX_TRANSPARENT;
        SetWindowLong(hwnd, GWL_EXSTYLE, style);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
