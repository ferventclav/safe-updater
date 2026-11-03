using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SafeUpdater.Core;

/// <summary>Chocolatey packages. Upgrades need admin, so they go to the elevated helper.</summary>
public static partial class ChocolateySource
{
    public static async Task<SourceResult> CheckAsync(CancellationToken ct)
    {
        string? choco = Tools.Choco();
        if (choco is null)
        {
            return new SourceResult { Source = UpdateSource.Chocolatey, Absent = true };
        }
        // Exit code 2 means "outdated packages found" when Chocolatey's enhanced exit codes are on.
        ProcResult r = await ProcessRunner.RunAsync(choco, ["outdated", "-r", "--ignore-unfound"], TimeSpan.FromMinutes(6), ct: ct);
        List<UpdateItem> items = ParseOutdated(r.Output);
        if (items.Count == 0 && r.ExitCode is not (0 or 2))
        {
            return new SourceResult { Source = UpdateSource.Chocolatey, Failed = true, Message = "Chocolatey could not check: " + r.Tail(1).FirstOrDefault(), Managed = ManagedIds() };
        }
        return new SourceResult
        {
            Source = UpdateSource.Chocolatey,
            Items = items,
            Managed = ManagedIds(),
            Message = items.Count == 0 ? "All Chocolatey packages are up to date" : null,
        };
    }

    /// <summary>"name|current|available|pinned" lines from "choco outdated -r".</summary>
    public static List<UpdateItem> ParseOutdated(string output)
    {
        var items = new List<UpdateItem>();
        foreach (string line in output.Split('\n').Select(l => l.Trim()))
        {
            string[] p = line.Split('|');
            if (p.Length < 4 || p[0].Length == 0 || Versions.Parse(p[2]) is null)
            {
                continue;
            }
            items.Add(new UpdateItem
            {
                Source = UpdateSource.Chocolatey,
                Id = p[0],
                Name = p[0],
                Current = p[1],
                Available = p[2],
                Pinned = p[3].Equals("true", StringComparison.OrdinalIgnoreCase),
                IsMajor = Versions.IsMajorJump(p[1], p[2]),
                NeedsAdmin = true,
            });
        }
        return items;
    }

    /// <summary>Every package Chocolatey has installed, read from its lib folder (works on Chocolatey 1 and 2).</summary>
    public static List<string> ManagedIds()
    {
        string lib = Path.Combine(Tools.ChocolateyRoot, "lib");
        return Directory.Exists(lib) ? Directory.GetDirectories(lib).Select(d => Path.GetFileName(d)).ToList() : [];
    }

    [GeneratedRegex(@"[^a-z0-9]")]
    private static partial Regex NonAlnum();

    private static string Squash(string s) => NonAlnum().Replace(s.ToLowerInvariant(), "");

    /// <summary>Popular Chocolatey packages whose names share nothing with the winget id (squashed choco id to squashed winget id start).</summary>
    private static readonly Dictionary<string, string> KnownPairs = new()
    {
        ["vscode"] = "microsoftvisualstudiocode",
        ["notepadplusplus"] = "notepadnotepad",
        ["nodejslts"] = "openjsnodejs",
        ["adobereader"] = "adobeacrobatreader",
        ["gh"] = "githubcli",
    };

    /// <summary>
    /// The Chocolatey package a winget app belongs to, if any. Matches the winget id's last part ("Git.Git" to "git"),
    /// the whole id ("Google.Chrome" to "googlechrome"), a name that is the package id plus a version ("7-Zip 26.04"
    /// to "7zip"), or a known pair ("vscode"), but not "GitHub CLI" to "git".
    /// </summary>
    public static string? OwnerOf(UpdateItem wingetItem, IEnumerable<string> chocoIds)
    {
        string whole = Squash(wingetItem.Id);
        string last = Squash(wingetItem.Id.Split('.').Last());
        string name = Squash(wingetItem.Name);
        foreach (string raw in chocoIds)
        {
            string id = Squash(Regex.Replace(raw, @"\.(install|portable|app)$", "", RegexOptions.IgnoreCase));
            if (KnownPairs.TryGetValue(id, out string? pair) && whole.StartsWith(pair, StringComparison.Ordinal))
            {
                return raw;
            }
            if (id.Length < 3 || id == "chocolatey")
            {
                continue;
            }
            if (last == id || whole == id || name == id
                || (name.StartsWith(id, StringComparison.Ordinal) && name.Length > id.Length && char.IsDigit(name[id.Length])))
            {
                return raw;
            }
        }
        return null;
    }

    public static List<string> UpgradeArgs(UpdateItem item) => ["upgrade", item.Id, "-y", "--no-progress", "-r"];
}

/// <summary>Scoop apps. They live in the user's profile, so they update without admin.</summary>
public static partial class ScoopSource
{
    internal const string StatusScript = """
        $ErrorActionPreference = 'Continue'
        scoop update *> $null
        $rows = foreach ($x in @(scoop status 6> $null)) {
            if ($x -is [string] -or -not $x.Name) { continue }
            [pscustomobject]@{ name = "$($x.Name)"; current = "$($x.'Installed Version')"; latest = "$($x.'Latest Version')"; info = "$($x.Info)" }
        }
        ConvertTo-Json -InputObject @($rows) -Compress
        """;

    [GeneratedRegex(@"^[A-Za-z0-9._\-]+$")]
    private static partial Regex SafeName();

    public static async Task<SourceResult> CheckAsync(CancellationToken ct)
    {
        if (Tools.Scoop() is null)
        {
            return new SourceResult { Source = UpdateSource.Scoop, Absent = true };
        }
        ProcResult r = await ProcessRunner.PowerShellAsync(StatusScript, TimeSpan.FromMinutes(6), ct);
        List<UpdateItem>? items = ParseStatus(r.Output);
        if (items is null)
        {
            return new SourceResult { Source = UpdateSource.Scoop, Failed = true, Message = "Scoop could not check: " + r.Tail(1).FirstOrDefault() };
        }
        return new SourceResult
        {
            Source = UpdateSource.Scoop,
            Items = items,
            Message = items.Count == 0 ? "All Scoop apps are up to date" : null,
        };
    }

    public static List<UpdateItem>? ParseStatus(string output)
    {
        string? json = output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith('[') || l.StartsWith('{'));
        if (json is null)
        {
            return null;
        }
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            IEnumerable<JsonElement> rows = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray() : [doc.RootElement];
            var items = new List<UpdateItem>();
            foreach (JsonElement e in rows.Where(e => e.ValueKind == JsonValueKind.Object))
            {
                string name = Str(e, "name"), current = Str(e, "current"), latest = Str(e, "latest"), info = Str(e, "info");
                if (name.Length == 0 || latest.Length == 0 || latest == current || !SafeName().IsMatch(name))
                {
                    continue;
                }
                items.Add(new UpdateItem
                {
                    Source = UpdateSource.Scoop,
                    Id = name,
                    Name = name,
                    Current = current,
                    Available = latest,
                    IsMajor = Versions.IsMajorJump(current, latest),
                    UnsafeReason = info.Contains("Held", StringComparison.OrdinalIgnoreCase) ? "Held in Scoop (scoop hold), so it stays at this version until you run scoop unhold."
                        : info.Contains("Global", StringComparison.OrdinalIgnoreCase) ? "Installed for all users (scoop install -g), which needs an admin terminal to update."
                        : info.Contains("fail", StringComparison.OrdinalIgnoreCase) ? "Its last install failed. Reinstall it with Scoop before updating it."
                        : null,
                });
            }
            return items;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string UpdateScript(UpdateItem item) =>
        SafeName().IsMatch(item.Id) ? $"scoop update '{item.Id}'" : throw new ArgumentException("Unexpected Scoop app name: " + item.Id);

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}

/// <summary>Warnings worth reading before installing anything (the same checks Windows Update makes).</summary>
public static class Preflight
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    public static List<string> Warnings()
    {
        var w = new List<string>();
        if (RebootPending())
        {
            w.Add("Windows is waiting for a restart. Installers can fail until then, so restart first if you can.");
        }
        if (GetSystemPowerStatus(out SystemPowerStatus p) && p.ACLineStatus == 0 && (p.BatteryFlag & 128) == 0)
        {
            string pct = p.BatteryLifePercent <= 100 ? $" ({p.BatteryLifePercent}%)" : "";
            w.Add($"This PC is on battery{pct}. Plug it in: an installer cut off by a flat battery can leave an app broken.");
        }
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!);
            double freeGb = drive.AvailableFreeSpace / 1024.0 / 1024 / 1024;
            if (freeGb < 10)
            {
                w.Add($"Only {freeGb:0.#} GB is free on {drive.Name.TrimEnd('\\')}. Large updates may fail to install.");
            }
        }
        catch (IOException)
        {
        }
        return w;
    }

    private static bool RebootPending()
    {
        foreach (string key in new[]
                 {
                     @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending",
                     @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired",
                 })
        {
            using RegistryKey? k = Registry.LocalMachine.OpenSubKey(key);
            if (k is not null)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Makes a restore point when System Restore is on. It never turns System Restore on.</summary>
    internal const string RestorePointScript = """
        $w = $null
        try {
            Checkpoint-Computer -Description 'Before Safe Updater' -RestorePointType MODIFY_SETTINGS -ErrorAction Stop -WarningVariable w -WarningAction SilentlyContinue
            if ($w) { "SKIPPED: $w" } else { "CREATED" }
        } catch { "FAILED: $($_.Exception.Message)" }
        """;

    public static string DescribeRestorePoint(string output)
    {
        string line = output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "";
        return line.StartsWith("CREATED", StringComparison.Ordinal) ? "Made a restore point before updating."
            : line.StartsWith("SKIPPED", StringComparison.Ordinal) ? "Windows already made a restore point in the last 24 hours, so it didn't make another."
            : "No restore point was made (System Restore may be off on this PC; Safe Updater doesn't turn it on).";
    }
}
