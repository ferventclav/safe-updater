using System.Text.RegularExpressions;

namespace SafeUpdater.Core;

/// <summary>Finds the command-line tools the checks need, using the PATH as it is in the registry right now.</summary>
public static partial class Tools
{
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static string PowerShell { get; } =
        Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    /// <summary>Machine PATH, then user PATH, then anything only this process had (WindowsApps aliases).</summary>
    public static string FreshPath()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parts = new List<string>();
        foreach (string? block in new[]
                 {
                     Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.Machine),
                     Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User),
                     Environment.GetEnvironmentVariable("Path"),
                 })
        {
            foreach (string raw in (block ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                string dir = Environment.ExpandEnvironmentVariables(raw.Trim());
                if (dir.Length > 0 && seen.Add(dir.TrimEnd('\\')))
                {
                    parts.Add(dir);
                }
            }
        }
        return string.Join(';', parts);
    }

    public static string? Find(string exe, bool skipWindowsApps = false)
    {
        foreach (string dir in FreshPath().Split(';'))
        {
            if (skipWindowsApps && dir.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            try
            {
                string candidate = Path.Combine(dir, exe);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
            }
        }
        return null;
    }

    public static string Winget() =>
        Find("winget.exe") ?? Path.Combine(LocalAppData, "Microsoft", "WindowsApps", "winget.exe");

    /// <summary>winget ships with App Installer; some PCs (LTSC, Server, stripped images) don't have it.</summary>
    public static bool HasWinget() => File.Exists(Winget());

    public static string? Npm() => Find("npm.cmd");

    public static string? Rustup() =>
        Find("rustup.exe") ?? Existing(Path.Combine(Home, ".cargo", "bin", "rustup.exe"));

    public static string? Claude() =>
        Find("claude.exe") ?? Find("claude.cmd") ?? Existing(Path.Combine(Home, ".local", "bin", "claude.exe"));

    public static string ChocolateyRoot =>
        Environment.GetEnvironmentVariable("ChocolateyInstall")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "chocolatey");

    public static string? Choco() =>
        Find("choco.exe") ?? Existing(Path.Combine(ChocolateyRoot, "bin", "choco.exe"));

    public static string? Scoop() =>
        Find("scoop.cmd")
        ?? Existing(Path.Combine(Environment.GetEnvironmentVariable("SCOOP") ?? Path.Combine(Home, "scoop"), "shims", "scoop.cmd"));

    /// <summary>True when a file can be created in the folder as the current user (ACLs, not just the read-only flag).</summary>
    public static bool CanWrite(string dir)
    {
        try
        {
            string probe = Path.Combine(dir, ".safeupdater-" + Guid.NewGuid().ToString("N"));
            using (File.Create(probe, 1, FileOptions.DeleteOnClose))
            {
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>The real python.exe (never the Microsoft Store stub), newest install first.</summary>
    public static string? Python()
    {
        string? onPath = Find("python.exe", skipWindowsApps: true);
        if (onPath is not null)
        {
            return onPath;
        }
        var roots = new List<string>();
        foreach (string parent in new[] { @"C:\", Path.Combine(LocalAppData, "Programs", "Python") })
        {
            if (Directory.Exists(parent))
            {
                roots.AddRange(Directory.GetDirectories(parent, "Python3*"));
            }
        }
        return roots
            .Select(d => (dir: d, minor: PythonMinor(d)))
            .OrderByDescending(x => x.minor)
            .Select(x => Path.Combine(x.dir, "python.exe"))
            .FirstOrDefault(File.Exists);
    }

    [GeneratedRegex(@"Python3(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex PythonDir();

    private static int PythonMinor(string dir)
    {
        Match m = PythonDir().Match(Path.GetFileName(dir));
        return m.Success && int.TryParse(m.Groups[1].Value, out int n) ? n : 0;
    }

    private static string? Existing(string path) => File.Exists(path) ? path : null;
}
