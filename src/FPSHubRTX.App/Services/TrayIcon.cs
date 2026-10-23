using System.Windows;
using FPSHubRTX.Core.Services;
using FPSHubRTX.ViewModels;
using WinForms = System.Windows.Forms;

namespace FPSHubRTX.App.Services;

/// <summary>
/// System-tray icon with a menu and Windows notifications (balloon notifications appear as Windows
/// toast notifications on Windows 10/11 and respect Focus Assist / Do Not Disturb).
/// </summary>
public sealed class TrayIcon : INotificationSink, IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private readonly MainViewModel _vm;
    private readonly Window _window;

    public TrayIcon(MainViewModel vm, Window window, AppHost host, Action exit)
    {
        _vm = vm;
        _window = window;
        _icon = new WinForms.NotifyIcon
        {
            Text = "FPS Hub RTX",
            Visible = true,
            Icon = LoadIcon(),
        };

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Open FPS Hub RTX", null, (_, _) => ShowWindow());
        menu.Items.Add("Scan games", null, (_, _) => { ShowWindow(); vm.Navigate(Pages.Scan + ":start"); });
        menu.Items.Add("Show / hide overlay", null, (_, _) => vm.ToggleOverlay());
        menu.Items.Add("Performance panel", null, (_, _) => { ShowWindow(); vm.Navigate(Pages.Performance); });
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => exit());
        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => ShowWindow();
        _icon.MouseClick += (_, e) => { if (e.Button == WinForms.MouseButtons.Left) ShowWindow(); };
    }

    private static System.Drawing.Icon LoadIcon()
    {
        try
        {
            var info = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/FPSHubRTX.ico"));
            if (info is not null)
            {
                using var stream = info.Stream;
                return new System.Drawing.Icon(stream);
            }
        }
        catch (Exception)
        {
        }
        return System.Drawing.SystemIcons.Application;
    }

    private void ShowWindow()
    {
        _window.Dispatcher.Invoke(() =>
        {
            if (!_window.IsVisible) _window.Show();
            if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
            _window.Activate();
        });
    }

    public void Show(string title, string message, NotificationLevel level)
    {
        var icon = level switch
        {
            NotificationLevel.Error => WinForms.ToolTipIcon.Error,
            NotificationLevel.Warning => WinForms.ToolTipIcon.Warning,
            _ => WinForms.ToolTipIcon.Info,
        };
        _window.Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                _icon.ShowBalloonTip(5000, title, message.Length > 250 ? message[..250] + "…" : message, icon);
            }
            catch (Exception)
            {
                // Notifications are best-effort.
            }
        }));
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
