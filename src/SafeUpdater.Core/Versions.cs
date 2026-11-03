using System.Text.RegularExpressions;

namespace SafeUpdater.Core;

/// <summary>Version strings as package managers print them: "24.15.0", "7.2.1 (48556)", "&lt; 175.0", "N-125365-g9a01".</summary>
public static partial class Versions
{
    [GeneratedRegex(@"^\s*v?(\d+(?:\.\d+)*)")]
    private static partial Regex Leading();

    /// <summary>The leading run of numbers, or null when the string does not start with one.</summary>
    public static int[]? Parse(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }
        Match m = Leading().Match(version);
        if (!m.Success)
        {
            return null;
        }
        string[] parts = m.Groups[1].Value.Split('.');
        var nums = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], out nums[i]))
            {
                return null;
            }
        }
        return nums;
    }

    /// <summary>winget prints "&lt; 175.0" or "Unknown" when it cannot read the installed version.</summary>
    public static bool IsUnknown(string? version) =>
        string.IsNullOrWhiteSpace(version)
        || version.TrimStart().StartsWith('<')
        || version.Trim().Equals("Unknown", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when the first number changes, or the second for 0.x versions (semver treats those as breaking).
    /// Calendar versions such as 2026.10.26 never count, and neither do strings without numbers.
    /// </summary>
    public static bool IsMajorJump(string? current, string? available)
    {
        if (IsUnknown(current))
        {
            return false;
        }
        int[]? a = Parse(current);
        int[]? b = Parse(available);
        if (a is null || b is null || a[0] >= 1990 || b[0] >= 1990)
        {
            return false;
        }
        if (a[0] != b[0])
        {
            return true;
        }
        return a[0] == 0 && Part(a, 1) != Part(b, 1);
    }

    public static int Compare(string? x, string? y)
    {
        int[] a = Parse(x) ?? [];
        int[] b = Parse(y) ?? [];
        for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            int c = Part(a, i).CompareTo(Part(b, i));
            if (c != 0)
            {
                return c;
            }
        }
        return 0;
    }

    /// <summary>"24.x" for 24.15.0, "0.13.x" for 0.13.1; what "same family" means in the window.</summary>
    public static string Family(string? version)
    {
        int[]? p = Parse(version);
        if (p is null)
        {
            return "";
        }
        return p[0] == 0 && p.Length > 1 ? $"0.{p[1]}.x" : $"{p[0]}.x";
    }

    public static bool SameFamily(string? a, string? b) => Family(a) != "" && Family(a) == Family(b);

    // Words anywhere ("1.0.0-beta", "5.0-preview.3"), or pip/semver suffixes straight after the numbers
    // ("2.0.0rc1", "3.13.0a1", "4.0.0.dev3", "1.0.0-rc.1"). a and b need a number after them, so OpenSSL-style
    // stable letters ("1.0.2a") don't match, and neither do build hashes like "N-127043-g5a54".
    [GeneratedRegex(@"(?i)(alpha|beta|preview|nightly|canary|insider)|^\s*v?\d+(\.\d+)*[-.]?((a|b)\.?\d+|(rc|pre|dev)\.?\d*)\b")]
    private static partial Regex PreRelease();

    public static bool IsPrerelease(string? version) => !string.IsNullOrWhiteSpace(version) && PreRelease().IsMatch(version);

    /// <summary>True only when both versions are readable and the "update" is older than what is installed.</summary>
    public static bool IsDowngrade(string? current, string? available) =>
        !IsUnknown(current) && Parse(current) is not null && Parse(available) is not null && Compare(available, current) < 0;

    private static int Part(int[] v, int i) => i < v.Length ? v[i] : 0;
}
