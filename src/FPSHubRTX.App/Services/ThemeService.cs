using System.Windows;
using System.Windows.Media;
using FPSHubRTX.Core.Models;

namespace FPSHubRTX.App.Services;

/// <summary>Swaps the colour tokens in Theme.xaml for the dark/light theme and the user's accent colour.</summary>
public static class ThemeService
{
    public static bool AnimationsEnabled { get; private set; } = true;

    public static void Apply(AppTheme theme, string accentHex, bool animations)
    {
        AnimationsEnabled = animations;
        var r = Application.Current.Resources;
        var dark = theme == AppTheme.Dark;

        Set(r, "Bg0Brush", dark ? "#0A0A10" : "#F3F4F9");
        Set(r, "Bg1Brush", dark ? "#0F0F17" : "#FFFFFF");
        Set(r, "CardBrush", dark ? "#14141E" : "#FFFFFF");
        Set(r, "CardHoverBrush", dark ? "#1B1B29" : "#EEEEF6");
        Set(r, "InputBrush", dark ? "#191924" : "#F5F5FB");
        Set(r, "LineBrush", dark ? "#262638" : "#DEDEEA");
        Set(r, "TextBrush", dark ? "#F4F4F8" : "#14141E");
        Set(r, "SubTextBrush", dark ? "#A6A6BC" : "#4A4A60");
        Set(r, "MutedBrush", dark ? "#6C6C84" : "#85859C");

        var accent = Parse(accentHex, Color.FromRgb(0x8B, 0x5C, 0xF6));
        r["AccentBrush"] = Freeze(new SolidColorBrush(accent));
        r["AccentSoftBrush"] = Freeze(new SolidColorBrush(Color.FromArgb(dark ? (byte)0x33 : (byte)0x26, accent.R, accent.G, accent.B)));
        var gradient = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        gradient.GradientStops.Add(new GradientStop(accent, 0));
        gradient.GradientStops.Add(new GradientStop(Color.FromRgb(0x3B, 0x82, 0xF6), 0.55));
        gradient.GradientStops.Add(new GradientStop(Color.FromRgb(0x22, 0xD3, 0xEE), 1));
        r["AccentGradient"] = Freeze(gradient);
    }

    public static Color Parse(string? hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        try
        {
            return (Color)ColorConverter.ConvertFromString(hex);
        }
        catch (Exception ex) when (ex is FormatException or NotSupportedException or InvalidOperationException)
        {
            return fallback;
        }
    }

    private static void Set(ResourceDictionary r, string key, string hex)
        => r[key] = Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)));

    private static T Freeze<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
