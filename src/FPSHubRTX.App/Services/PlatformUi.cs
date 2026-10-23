using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using FPSHubRTX.App.Dialogs;
using FPSHubRTX.App.Views;
using FPSHubRTX.Core.Models;
using FPSHubRTX.Core.Services;
using FPSHubRTX.ViewModels;

namespace FPSHubRTX.App.Services;

/// <summary>WPF implementation of everything the view models need from the UI layer.</summary>
public sealed class PlatformUi : IPlatformUi
{
    private const int WM_HOTKEY = 0x0312;
    private const int HotkeyToggleId = 0x4650;
    private const int HotkeyEditId = 0x4651;

    private readonly MainWindow _main;
    private readonly Dispatcher _dispatcher;
    private OverlayWindow? _overlay;
    private AppHost? _host;
    private HwndSource? _hotkeySource;
    private Action? _onToggle;
    private Action? _onEdit;
    private readonly List<int> _registered = new();

    public PlatformUi(MainWindow main, Dispatcher dispatcher)
    {
        _main = main;
        _dispatcher = dispatcher;
    }

    public void AttachOverlay(OverlayWindow overlay, AppHost host)
    {
        _overlay = overlay;
        _host = host;
        overlay.PositionSaved += (left, top) =>
        {
            host.S.Overlay.Corner = OverlayCorner.Custom;
            host.S.Overlay.CustomLeft = left;
            host.S.Overlay.CustomTop = top;
            host.Settings.NotifyChanged();
        };
    }

    public void Post(Action action)
    {
        if (_dispatcher.HasShutdownStarted) return;
        _dispatcher.BeginInvoke(action);
    }

    private Window Owner => _main.IsVisible ? _main : (Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? _main);

    public string? PickFolder(string title)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = title, Multiselect = false };
        return dlg.ShowDialog(Owner) == true ? dlg.FolderName : null;
    }

    public string? PickFile(string title, string filter)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true, Multiselect = false };
        return dlg.ShowDialog(Owner) == true ? dlg.FileName : null;
    }

    public string? SaveFile(string title, string filter, string defaultFileName)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { Title = title, Filter = filter, FileName = defaultFileName, OverwritePrompt = true, AddExtension = true };
        return dlg.ShowDialog(Owner) == true ? dlg.FileName : null;
    }

    public bool Confirm(string title, string message)
        => MessageBox.Show(Owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

    public void ShowInfo(string title, string message)
        => MessageBox.Show(Owner, message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public void ShowError(string title, string message)
        => MessageBox.Show(Owner, message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    public string? Prompt(string title, string message, string defaultValue)
    {
        var dlg = new PromptWindow(title, message, defaultValue) { Owner = Owner };
        return dlg.ShowDialog() == true ? dlg.Value : null;
    }

    public bool EditGame(GameEditorViewModel editor)
    {
        var dlg = new GameEditorWindow { DataContext = editor, Owner = Owner };
        return dlg.ShowDialog() == true;
    }

    public void ApplyTheme(AppTheme theme, string accentHex, bool animations) => ThemeService.Apply(theme, accentHex, animations);

    public void SetOverlayVisible(bool visible)
    {
        if (_overlay is null) return;
        if (visible)
        {
            if (!_overlay.IsVisible) _overlay.Show();
            if (_host is not null) _overlay.ApplySettings(_host.S.Overlay);
        }
        else if (_overlay.IsVisible)
        {
            _overlay.Hide();
        }
    }

    public void ApplyOverlaySettings(OverlaySettings settings) => _overlay?.ApplySettings(settings);

    public void SetOverlayEditMode(bool editMode) => _overlay?.SetEditMode(editMode);

    /// <summary>System-wide hotkeys through the documented RegisterHotKey API.</summary>
    public string? RegisterHotkeys(string toggleOverlay, string editOverlay, Action onToggle, Action onEdit)
    {
        _onToggle = onToggle;
        _onEdit = onEdit;
        EnsureHotkeyWindow();
        if (_hotkeySource is null) return "Hotkeys unavailable (no window handle).";

        foreach (var id in _registered) UnregisterHotKey(_hotkeySource.Handle, id);
        _registered.Clear();

        var errors = new List<string>();
        Register(HotkeyToggleId, toggleOverlay, "overlay toggle", errors);
        Register(HotkeyEditId, editOverlay, "overlay edit", errors);
        return errors.Count == 0 ? null : string.Join(" ", errors);
    }

    private void Register(int id, string text, string label, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (!HotkeyGesture.TryParse(text, out var gesture))
        {
            errors.Add($"'{text}' isn't a valid {label} hotkey (use e.g. Ctrl+Shift+F10).");
            return;
        }
        if (RegisterHotKey(_hotkeySource!.Handle, id, gesture.Modifiers | HotkeyGesture.ModNoRepeat, gesture.VirtualKey))
            _registered.Add(id);
        else
            errors.Add($"{gesture.Text} is already used by another app — choose a different {label} hotkey.");
    }

    private void EnsureHotkeyWindow()
    {
        if (_hotkeySource is not null) return;
        // A dedicated message-only window so hotkeys work even while the main window is hidden in the tray.
        var parameters = new HwndSourceParameters("FPSHubRTX hotkeys") { Width = 0, Height = 0, WindowStyle = 0, ParentWindow = new IntPtr(-3) /* HWND_MESSAGE */ };
        _hotkeySource = new HwndSource(parameters);
        _hotkeySource.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            var id = wParam.ToInt32();
            if (id == HotkeyToggleId) _onToggle?.Invoke();
            else if (id == HotkeyEditId) _onEdit?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void SetClipboardText(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (COMException)
        {
            // Clipboard busy — ignore.
        }
    }

    public void ShowMainWindow()
    {
        if (!_main.IsVisible) _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
        _main.Topmost = true;  // bring to front reliably
        _main.Topmost = false;
        _main.Focus();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
