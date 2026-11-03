using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SafeUpdater.Core;

/// <summary>
/// Keeps command-line tools reachable across updates. On 3 Nov 2026 force-replacing yt-dlp's FFmpeg deleted the
/// shared ffmpeg.exe link in WinGet\Links and left a user PATH entry pointing at a deleted version folder.
/// A snapshot is taken before updating; afterwards missing links are recreated and dead PATH entries are moved
/// to the package's new version folder.
/// </summary>
public static class PathHealth
{
    public sealed class Snapshot
    {
        /// <summary>Link file name in WinGet\Links to the file it pointed at.</summary>
        public Dictionary<string, string> Links { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>User PATH entries inside WinGet\Packages, with the .exe names each held.</summary>
        public Dictionary<string, List<string>> PackageDirs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public sealed record Repair(string Kind, string Path, string Target, string Description);

    private static string WingetRoot => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet");

    public static string LinksDir => System.IO.Path.Combine(WingetRoot, "Links");
    public static string PackagesDir => System.IO.Path.Combine(WingetRoot, "Packages");

    public static Snapshot Take() => Take(UserPathEntries(), LinksDir, PackagesDir);

    public static Snapshot Take(IEnumerable<string> pathEntries, string linksDir, string packagesDir)
    {
        var snap = new Snapshot();
        if (Directory.Exists(linksDir))
        {
            foreach (string file in Directory.GetFiles(linksDir))
            {
                string? target = new FileInfo(file).LinkTarget;
                if (target is not null)
                {
                    snap.Links[System.IO.Path.GetFileName(file)] = System.IO.Path.GetFullPath(target, linksDir);
                }
            }
        }
        foreach (string entry in pathEntries)
        {
            if (IsUnder(entry, packagesDir) && Directory.Exists(entry))
            {
                snap.PackageDirs[entry] = Directory.GetFiles(entry, "*.exe").Select(f => System.IO.Path.GetFileName(f)).ToList();
            }
        }
        return snap;
    }

    public static List<Repair> Plan(Snapshot before, IEnumerable<string> pathEntriesNow, string linksDir, string packagesDir)
    {
        var plan = new List<Repair>();
        foreach ((string name, string target) in before.Links)
        {
            string link = System.IO.Path.Combine(linksDir, name);
            if (File.Exists(link) || new FileInfo(link).LinkTarget is not null)
            {
                continue;
            }
            string? to = File.Exists(target) ? target : NewestInPackage(target, packagesDir, [name]);
            if (to is not null)
            {
                plan.Add(new Repair("link", link, to, $"Recreated the {System.IO.Path.GetFileNameWithoutExtension(name)} command"));
            }
        }
        foreach (string entry in pathEntriesNow)
        {
            if (Directory.Exists(entry) || !before.PackageDirs.TryGetValue(entry, out List<string>? exes) || exes.Count == 0)
            {
                continue;
            }
            string? dir = NewestInPackage(System.IO.Path.Combine(entry, exes[0]), packagesDir, exes);
            if (dir is not null)
            {
                plan.Add(new Repair("path", entry, System.IO.Path.GetDirectoryName(dir)!, $"Pointed PATH at the new {PackageName(entry, packagesDir)} folder"));
            }
        }
        return plan;
    }

    /// <summary>Applies a plan. Needs admin for links unless Developer Mode is on.</summary>
    public static List<string> Apply(List<Repair> plan)
    {
        var done = new List<string>();
        foreach (Repair r in plan.Where(r => r.Kind == "link"))
        {
            try
            {
                File.CreateSymbolicLink(r.Path, r.Target);
                done.Add(r.Description);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                done.Add($"Could not recreate {System.IO.Path.GetFileName(r.Path)}: {ex.Message}");
            }
        }
        List<Repair> paths = plan.Where(r => r.Kind == "path").ToList();
        if (paths.Count > 0)
        {
            using RegistryKey? env = Registry.CurrentUser.OpenSubKey("Environment", writable: true);
            if (env is not null)
            {
                string raw = env.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
                RegistryValueKind kind = env.GetValueKind("Path");
                List<string> parts = raw.Split(';').ToList();
                foreach (Repair r in paths)
                {
                    int i = parts.FindIndex(p => string.Equals(Environment.ExpandEnvironmentVariables(p).TrimEnd('\\'), r.Path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
                    if (i >= 0)
                    {
                        parts[i] = r.Target;
                        done.Add(r.Description);
                    }
                }
                env.SetValue("Path", string.Join(';', parts), kind == RegistryValueKind.ExpandString ? RegistryValueKind.ExpandString : RegistryValueKind.String);
                BroadcastEnvironmentChange();
            }
        }
        return done;
    }

    public static List<string> UserPathEntries()
    {
        using RegistryKey? env = Registry.CurrentUser.OpenSubKey("Environment");
        string raw = env?.GetValue("Path", "") as string ?? "";
        return raw.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(p => Environment.ExpandEnvironmentVariables(p.Trim())).ToList();
    }

    /// <summary>Inside the same WinGet package folder, the newest directory that holds every one of <paramref name="exes"/>.</summary>
    private static string? NewestInPackage(string oldFile, string packagesDir, IReadOnlyList<string> exes)
    {
        string? root = PackageRoot(oldFile, packagesDir);
        if (root is null || !Directory.Exists(root))
        {
            return null;
        }
        return Directory.GetFiles(root, exes[0], SearchOption.AllDirectories)
            .Select(f => System.IO.Path.GetDirectoryName(f)!)
            .Where(d => exes.All(e => File.Exists(System.IO.Path.Combine(d, e))))
            .Select(d => new DirectoryInfo(d))
            .OrderByDescending(d => d.LastWriteTimeUtc)
            .Select(d => System.IO.Path.Combine(d.FullName, exes[0]))
            .FirstOrDefault();
    }

    private static string? PackageRoot(string path, string packagesDir)
    {
        if (!IsUnder(path, packagesDir))
        {
            return null;
        }
        string rest = System.IO.Path.GetRelativePath(packagesDir, path);
        string first = rest.Split(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)[0];
        return System.IO.Path.Combine(packagesDir, first);
    }

    private static string PackageName(string path, string packagesDir)
    {
        string? root = PackageRoot(path, packagesDir);
        string name = root is null ? "package" : System.IO.Path.GetFileName(root);
        int cut = name.IndexOf("_Microsoft.Winget", StringComparison.OrdinalIgnoreCase);
        return cut > 0 ? name[..cut] : name;
    }

    private static bool IsUnder(string path, string dir) =>
        System.IO.Path.GetFullPath(path).StartsWith(System.IO.Path.GetFullPath(dir).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, UIntPtr wParam, string lParam, uint flags, uint timeout, out UIntPtr result);

    private static void BroadcastEnvironmentChange() =>
        SendMessageTimeout(new IntPtr(0xffff), 0x001A, UIntPtr.Zero, "Environment", 0x0002, 5000, out _);
}
