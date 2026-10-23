using System.Text.RegularExpressions;

namespace FPSHubRTX.Core.Diagnostics;

/// <summary>
/// Removes personal and secret data from text before it is logged or put in a diagnostic report:
/// the Windows user name / profile path, e-mail addresses, and anything that looks like an access token.
/// </summary>
public static partial class LogSanitizer
{
    private static string? _userName = SafeUserName();
    private static string? _userProfile = SafeUserProfile();

    /// <summary>For tests: override the user name/profile that gets redacted.</summary>
    internal static void Configure(string? userName, string? userProfile)
    {
        _userName = userName;
        _userProfile = userProfile;
    }

    public static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var result = text;

        if (!string.IsNullOrEmpty(_userProfile) && _userProfile.Length > 3)
        {
            result = result.Replace(_userProfile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
            result = result.Replace(_userProfile.Replace('\\', '/'), "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        }

        // Any other C:\Users\<name>\ path (e.g. another account).
        result = UsersPathRegex().Replace(result, m => m.Groups[1].Value + "<user>" + m.Groups[3].Value);

        if (!string.IsNullOrEmpty(_userName) && _userName.Length >= 2)
        {
            // "DOMAIN\user" or "user=..." style occurrences. Bare words are left alone so that
            // short user names such as "max" don't destroy messages like "max FPS".
            result = Regex.Replace(result, $@"(?<=[\\/])({Regex.Escape(_userName)})\b", "<user>", RegexOptions.IgnoreCase);
            result = Regex.Replace(result, $@"(?<=user(?:name)?\s*[=:]\s*){Regex.Escape(_userName)}\b", "<user>", RegexOptions.IgnoreCase);
        }

        result = EmailRegex().Replace(result, "<email>");
        result = TokenRegex().Replace(result, m => m.Groups[1].Value + "<redacted>");
        result = BearerRegex().Replace(result, "Bearer <redacted>");
        return result;
    }

    [GeneratedRegex(@"([A-Za-z]:[\\/]Users[\\/])([^\\/""'\s]+)([\\/]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex UsersPathRegex();

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}")]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"((?:access_?token|refresh_?token|auth_?token|api_?key|password|passwd|secret|session_?id|ticket)\s*[=:]\s*""?)[^\s""&;,]+", RegexOptions.IgnoreCase)]
    private static partial Regex TokenRegex();

    [GeneratedRegex(@"Bearer\s+[A-Za-z0-9\-._~+/]+=*", RegexOptions.IgnoreCase)]
    private static partial Regex BearerRegex();

    private static string? SafeUserName()
    {
        try { return Environment.UserName; } catch { return null; }
    }

    private static string? SafeUserProfile()
    {
        try { return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); } catch { return null; }
    }
}
