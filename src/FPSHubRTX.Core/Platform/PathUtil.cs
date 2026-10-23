namespace FPSHubRTX.Core.Platform;

/// <summary>
/// Path helpers. Every path coming from a launcher manifest, the registry or the user is
/// treated as untrusted: it is validated before it is used and never executed blindly.
/// </summary>
public static class PathUtil
{
    private static readonly char[] Separators = { '\\', '/' };

    /// <summary>Canonical comparison key: full path, unified separators, no trailing separator, upper-case.</summary>
    public static string NormalizeKey(string path)
    {
        var p = path.Trim().Replace('/', '\\');
        try
        {
            if (Path.IsPathRooted(path)) p = Path.GetFullPath(path).Replace('/', '\\');
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
        }
        p = p.TrimEnd('\\');
        if (p.Length == 2 && p[1] == ':') p += "\\";
        return p.ToUpperInvariant();
    }

    /// <summary>Converts launcher-style forward slashes ("C:/Riot Games/VALORANT") to the native separator.</summary>
    public static string ToNative(string path)
        => path.Replace(Path.DirectorySeparatorChar == '\\' ? '/' : '\\', Path.DirectorySeparatorChar);

    /// <summary>True when <paramref name="candidate"/> is the same as or inside <paramref name="directory"/>.</summary>
    public static bool IsUnder(string candidate, string directory)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(directory)) return false;
        var c = NormalizeKey(candidate);
        var d = NormalizeKey(directory);
        if (c == d) return true;
        if (!d.EndsWith('\\')) d += "\\";
        return c.StartsWith(d, StringComparison.Ordinal);
    }

    /// <summary>
    /// Basic safety validation for a path read from an untrusted source: absolute, no control characters,
    /// no device namespace prefixes, valid characters.
    /// </summary>
    public static bool IsSafeAbsolutePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (path.Length > 32_000) return false;
        if (path.Any(char.IsControl)) return false;
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal)) return false;
        if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return false;
        if (path.IndexOfAny(new[] { '<', '>', '|', '"', '*', '?' }) >= 0) return false;
        try
        {
            return Path.IsPathFullyQualified(path) || (Path.IsPathRooted(path) && !OperatingSystem.IsWindows());
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static string? SafeCombine(string? root, params string[] parts)
    {
        if (string.IsNullOrWhiteSpace(root)) return null;
        try
        {
            var combined = Path.Combine(new[] { root }.Concat(parts.Select(p => p.TrimStart(Separators))).ToArray());
            var full = Path.GetFullPath(combined);
            // Refuse "..\" escapes out of the root.
            return IsUnder(full, root) ? full : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    public static bool DirectoryExistsSafe(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try { return Directory.Exists(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    public static bool FileExistsSafe(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try { return File.Exists(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    /// <summary>Reads a small text file safely; returns null on any error or if the file exceeds the size limit.</summary>
    public static string? ReadTextSafe(string path, long maxBytes = 8 * 1024 * 1024)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > maxBytes) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    public static IEnumerable<string> EnumerateFilesSafe(string dir, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(dir, pattern, new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false, MatchCasing = MatchCasing.CaseInsensitive }).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Array.Empty<string>();
        }
    }

    public static IEnumerable<string> EnumerateDirectoriesSafe(string dir)
    {
        try
        {
            return Directory.EnumerateDirectories(dir, "*", new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false, AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint }).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>Simple stable 64-bit FNV-1a hash rendered as hex, used for folder-based game ids.</summary>
    public static string StableHash(string text)
    {
        ulong hash = 14695981039346656037UL;
        foreach (var ch in text)
        {
            hash ^= ch;
            hash *= 1099511628211UL;
        }
        return hash.ToString("x16");
    }
}
