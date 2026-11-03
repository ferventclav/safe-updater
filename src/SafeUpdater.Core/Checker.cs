using System.Diagnostics;
using System.Text.RegularExpressions;

namespace SafeUpdater.Core;

public static class Checker
{
    public static IReadOnlyList<SourceCheck> Sources { get; } =
    [
        new(UpdateSource.Apps, WingetSource.CheckAsync),
        new(UpdateSource.Chocolatey, ChocolateySource.CheckAsync),
        new(UpdateSource.Scoop, ScoopSource.CheckAsync),
        new(UpdateSource.Windows, WindowsUpdateSource.CheckAsync),
        new(UpdateSource.Nvidia, NvidiaSource.CheckAsync),
        new(UpdateSource.Python, PythonSource.CheckAsync),
        new(UpdateSource.Node, NodeSource.CheckAsync),
        new(UpdateSource.Rust, RustSource.CheckAsync),
        new(UpdateSource.ClaudeCode, ClaudeCodeSource.CheckAsync),
    ];

    public static string Title(UpdateSource s) => s switch
    {
        UpdateSource.Apps => "Apps",
        UpdateSource.Chocolatey => "Chocolatey packages",
        UpdateSource.Scoop => "Scoop apps",
        UpdateSource.Windows => "Windows and drivers",
        UpdateSource.Nvidia => "NVIDIA graphics driver",
        UpdateSource.Python => "Python packages",
        UpdateSource.Node => "Node packages",
        UpdateSource.Rust => "Rust",
        UpdateSource.ClaudeCode => "Claude Code",
        _ => s.ToString(),
    };

    public static string Describe(UpdateSource s) => s switch
    {
        UpdateSource.Apps => "Installed through winget, the Windows package manager.",
        UpdateSource.Chocolatey => "Installed with Chocolatey. Updating needs admin, so it is part of the one admin step.",
        UpdateSource.Scoop => "Installed with Scoop, in your user folder.",
        UpdateSource.Windows => "Windows updates and drivers checked against the ones you have, plus a nudge to the Microsoft Store.",
        UpdateSource.Nvidia => "Read from the NVIDIA app. Drivers are installed there.",
        UpdateSource.Python => "pip packages, updated where each one is installed. A snapshot is saved first, so you can roll back.",
        UpdateSource.Node => "Global npm packages.",
        UpdateSource.Rust => "rustup and its toolchains.",
        UpdateSource.ClaudeCode => "The claude command-line tool. The desktop app updates itself.",
        _ => "",
    };
}

/// <summary>The exact command each item runs, shown in the details pane so nothing is hidden.</summary>
public static class Commands
{
    public static string Describe(UpdateItem i) => i.Source switch
    {
        UpdateSource.Apps => "winget " + string.Join(' ', WingetParser.UpgradeArgs(i, force: false).Select(Quote)),
        UpdateSource.Chocolatey => "choco " + string.Join(' ', ChocolateySource.UpgradeArgs(i)),
        UpdateSource.Scoop => $"scoop update {i.Id}",
        UpdateSource.Windows when i.Kind == ItemKind.StoreScan => "MDM AppManagement UpdateScanMethod (asks the Store to update every app)",
        UpdateSource.Windows => "Windows Update API: download and install update " + i.Id,
        UpdateSource.Nvidia => "Open the NVIDIA app and install it there",
        UpdateSource.Python => $"python -m pip install{(i.Scope == "user" ? " --user" : "")} --upgrade {i.Id}=={i.Available}",
        UpdateSource.Node => $"npm install -g {i.Id}@{i.Available}",
        UpdateSource.Rust => i.Id == "rustup" ? "rustup self update" : $"rustup update {i.Id}",
        UpdateSource.ClaudeCode => "claude update",
        _ => "",
    };

    private static string Quote(string a) => a.Contains(' ') ? $"\"{a}\"" : a;
}

/// <summary>Open apps whose installer is about to run, so their windows can be saved and closed first.</summary>
public static partial class RunningApps
{
    private static readonly HashSet<string> Ignore = new(StringComparer.OrdinalIgnoreCase)
    {
        "microsoft", "windows", "nvidia", "google", "desktop", "runtime", "edition", "shared", "framework", "tools",
    };

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["libreoffice"] = "soffice",
        ["steelseries"] = "SteelSeriesGG",
    };

    [GeneratedRegex(@"[A-Za-z][A-Za-z0-9]+")]
    private static partial Regex Token();

    public static List<string> Find(IEnumerable<UpdateItem> items)
    {
        string[] running = Process.GetProcesses().Select(p => p.ProcessName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return Find(items, running);
    }

    public static List<string> Find(IEnumerable<UpdateItem> items, IReadOnlyCollection<string> running)
    {
        var open = new List<string>();
        foreach (UpdateItem i in items.Where(i => i.Source == UpdateSource.Apps))
        {
            foreach (string word in Token().Matches(i.Name).Select(m => m.Value).Where(w => w.Length >= 4 && !Ignore.Contains(w)))
            {
                string probe = Aliases.TryGetValue(word, out string? alias) ? alias : word;
                if (running.Any(p => p.StartsWith(probe, StringComparison.OrdinalIgnoreCase)))
                {
                    open.Add(i.Name);
                    break;
                }
            }
        }
        return open.Distinct().ToList();
    }
}
