using FPSHubRTX.Core.Models;
using FPSHubRTX.Core.Mvvm;

namespace FPSHubRTX.ViewModels;

/// <summary>Everything the view models need from the WPF layer (dialogs, theming, overlay, hotkeys).</summary>
public interface IPlatformUi : IUiDispatcher
{
    string? PickFolder(string title);
    /// <param name="filter">WPF/Win32 filter, e.g. "Programs (*.exe)|*.exe".</param>
    string? PickFile(string title, string filter);
    string? SaveFile(string title, string filter, string defaultFileName);
    bool Confirm(string title, string message);
    void ShowInfo(string title, string message);
    void ShowError(string title, string message);
    string? Prompt(string title, string message, string defaultValue);
    bool EditGame(GameEditorViewModel editor);

    void ApplyTheme(AppTheme theme, string accentHex, bool animations);
    void SetOverlayVisible(bool visible);
    void ApplyOverlaySettings(OverlaySettings settings);
    void SetOverlayEditMode(bool editMode);

    /// <summary>Registers global hotkeys; returns a user-facing error or null on success.</summary>
    string? RegisterHotkeys(string toggleOverlay, string editOverlay, Action onToggle, Action onEdit);

    void SetClipboardText(string text);
    void ShowMainWindow();
}
