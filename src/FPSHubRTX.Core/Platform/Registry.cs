using Microsoft.Win32;

namespace FPSHubRTX.Core.Platform;

/// <summary>Registry hive/view combinations the scanners read from.</summary>
public enum RegHive
{
    CurrentUser,
    /// <summary>HKLM, 64-bit view.</summary>
    LocalMachine,
    /// <summary>HKLM, 32-bit view (WOW6432Node) — where most launchers register themselves.</summary>
    LocalMachine32,
    /// <summary>HKCU, 32-bit view.</summary>
    CurrentUser32,
}

/// <summary>Read-only registry access, abstracted so scanners can be unit tested on any OS.</summary>
public interface IRegistryReader
{
    string? GetString(RegHive hive, string keyPath, string? valueName);
    IReadOnlyList<string> GetSubKeyNames(RegHive hive, string keyPath);
    IReadOnlyList<string> GetValueNames(RegHive hive, string keyPath);
}

/// <summary>Real registry. Returns nothing (never throws) on non-Windows or on access errors.</summary>
public sealed class WindowsRegistryReader : IRegistryReader
{
    public string? GetString(RegHive hive, string keyPath, string? valueName)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var key = Open(hive, keyPath);
            var value = key?.GetValue(valueName, null, RegistryValueOptions.None);
            return value switch
            {
                null => null,
                string s => s,
                string[] arr => string.Join(";", arr),
                byte[] => null,
                _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture),
            };
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    public IReadOnlyList<string> GetSubKeyNames(RegHive hive, string keyPath)
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<string>();
        try
        {
            using var key = Open(hive, keyPath);
            return key?.GetSubKeyNames() ?? Array.Empty<string>();
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return Array.Empty<string>();
        }
    }

    public IReadOnlyList<string> GetValueNames(RegHive hive, string keyPath)
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<string>();
        try
        {
            using var key = Open(hive, keyPath);
            return key?.GetValueNames() ?? Array.Empty<string>();
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return Array.Empty<string>();
        }
    }

    private static RegistryKey? Open(RegHive hive, string keyPath)
    {
        var (root, view) = hive switch
        {
            RegHive.CurrentUser => (RegistryHive.CurrentUser, RegistryView.Registry64),
            RegHive.CurrentUser32 => (RegistryHive.CurrentUser, RegistryView.Registry32),
            RegHive.LocalMachine => (RegistryHive.LocalMachine, RegistryView.Registry64),
            RegHive.LocalMachine32 => (RegistryHive.LocalMachine, RegistryView.Registry32),
            _ => (RegistryHive.CurrentUser, RegistryView.Default),
        };
        using var baseKey = RegistryKey.OpenBaseKey(root, view);
        return baseKey.OpenSubKey(keyPath, writable: false);
    }
}

/// <summary>Registry stand-in used by tests and on non-Windows hosts.</summary>
public sealed class InMemoryRegistry : IRegistryReader
{
    // Key = "Hive|Path" (case-insensitive like the real registry); original casing is preserved for enumeration.
    private readonly Dictionary<string, Dictionary<string, string>> _keys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _originalNames = new(StringComparer.OrdinalIgnoreCase);

    private static string K(RegHive hive, string path) => $"{hive}|{path.Trim('\\')}";

    public InMemoryRegistry Set(RegHive hive, string keyPath, string valueName, string value)
    {
        var parts = keyPath.Trim('\\').Split('\\');
        for (var i = 1; i <= parts.Length; i++)
        {
            var partial = string.Join('\\', parts.Take(i));
            var k = K(hive, partial);
            if (!_keys.ContainsKey(k))
            {
                _keys[k] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                _originalNames[k] = partial;
            }
        }
        _keys[K(hive, keyPath)][valueName] = value;
        return this;
    }

    public string? GetString(RegHive hive, string keyPath, string? valueName)
        => _keys.TryGetValue(K(hive, keyPath), out var values) && values.TryGetValue(valueName ?? "", out var v) ? v : null;

    public IReadOnlyList<string> GetSubKeyNames(RegHive hive, string keyPath)
    {
        var prefix = K(hive, keyPath) + "\\";
        return _keys.Keys
            .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(k => _originalNames[k].Split('\\').Last())
            .Where((name, _) => true)
            .Where(name => _keys.ContainsKey(prefix + name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<string> GetValueNames(RegHive hive, string keyPath)
        => _keys.TryGetValue(K(hive, keyPath), out var values) ? values.Keys.ToList() : Array.Empty<string>();
}
