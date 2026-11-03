using System.Text.RegularExpressions;

namespace SafeUpdater.Core;

public enum WingetOutcome { Ok, OkReboot, AlreadyCurrent, RetryForce, RetryNonAdmin, InUse, Failed }

/// <summary>Reads winget's fixed-width tables. winget has no JSON output for upgrades.</summary>
public static partial class WingetParser
{
    public sealed record Row(string Name, string Id, string Version, string Available, string Source, bool Pinned);

    [GeneratedRegex(@"\S+")]
    private static partial Regex Word();

    // HRESULTs seen on real runs. Checked before any text, because winget's messages are translated.
    private const int PortableModified = unchecked((int)0x8A150057);
    private const int ShellExecInstallFailed = unchecked((int)0x8A150006);
    private const int UpdateNotApplicable = unchecked((int)0x8A15002B);
    private const int RebootRequiredToFinish = unchecked((int)0x8A150109);

    /// <summary>
    /// Works in any display language: the header is whatever line sits on the dashes, columns come from where its
    /// words start, and a second table is always the "needs explicit targeting" (pinned) list.
    /// </summary>
    public static List<Row> ParseUpgradeTable(string output)
    {
        var rows = new List<Row>();
        List<string> lines = output.Replace("\r\n", "\n").Split('\n').Select(ProcessRunner.CleanLine).ToList();
        int table = 0;
        for (int i = 0; i < lines.Count - 1; i++)
        {
            if (!IsDashes(lines[i + 1]))
            {
                continue;
            }
            int[] starts = Word().Matches(lines[i]).Select(m => m.Index).ToArray();
            if (starts.Length < 4)
            {
                continue;
            }
            table++;
            for (int j = i + 2; j < lines.Count; j++)
            {
                string d = lines[j];
                if (string.IsNullOrWhiteSpace(d))
                {
                    break;
                }
                // The "28 upgrades available." footer is too short to reach the Id column.
                if (d.Length <= starts[1])
                {
                    continue;
                }
                List<string> cells = Cut(d, starts);
                string id = cells[1];
                // A cut-off id ("Microsoft.VisualStudio.Bui…") cannot be targeted, so it is left out.
                if (id.Length == 0 || id.Contains(' ') || id.EndsWith('…') || cells[2].Length == 0 || cells[3].Length == 0)
                {
                    continue;
                }
                rows.Add(new Row(cells[0], id, cells[2], cells[3], cells.Count > 4 ? cells[4] : "", table > 1));
            }
            i++;
        }
        return rows;
    }

    /// <summary>Versions listed by "winget show --id X --versions", newest first as winget prints them.</summary>
    public static List<string> ParseVersions(string output)
    {
        List<string> lines = output.Replace("\r\n", "\n").Split('\n').Select(ProcessRunner.CleanLine).ToList();
        // Here the rule under "Version" is only as wide as the column ("-------").
        int dash = lines.FindIndex(l => l.Trim().Length >= 3 && l.Trim().All(c => c == '-'));
        return dash < 0
            ? []
            : lines.Skip(dash + 1).Select(l => l.Trim()).Where(l => l.Length > 0 && Versions.Parse(l) is not null).ToList();
    }

    /// <summary>The newest version in the same family as <paramref name="current"/> that is newer than it.</summary>
    public static string? HighestInFamily(IEnumerable<string> versions, string current) =>
        versions
            .Where(v => Versions.SameFamily(v, current) && Versions.Compare(v, current) > 0)
            .OrderByDescending(v => v, Comparer<string>.Create(Versions.Compare))
            .FirstOrDefault();

    public static WingetOutcome Classify(int exitCode, string output, bool elevated)
    {
        bool Has(string s) => output.Contains(s, StringComparison.OrdinalIgnoreCase);

        // A portable package whose files changed since install refuses to upgrade without --force.
        if (exitCode == PortableModified || (Has("has been modified") && Has("--force")))
        {
            return WingetOutcome.RetryForce;
        }
        if (exitCode == 0)
        {
            return Has("restart your pc") || Has("restart is required") || Has("reboot") ? WingetOutcome.OkReboot : WingetOutcome.Ok;
        }
        if (exitCode == RebootRequiredToFinish)
        {
            return WingetOutcome.OkReboot;
        }
        if (exitCode == UpdateNotApplicable || Has("No available upgrade found") || Has("No newer package versions are available"))
        {
            return WingetOutcome.AlreadyCurrent;
        }
        if (Has("Access is denied") || Has("being used by another process"))
        {
            return WingetOutcome.InUse;
        }
        // Some installers crash when started as admin (GeForce NOW: 0xC0000005); they work as the user.
        if (elevated && (exitCode == ShellExecInstallFailed || Has("Installer failed with exit code")))
        {
            return WingetOutcome.RetryNonAdmin;
        }
        return WingetOutcome.Failed;
    }

    public static List<string> UpgradeArgs(UpdateItem item, bool force)
    {
        var args = new List<string>
        {
            "upgrade", "--id", item.Id, "-e", "--silent",
            "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity",
        };
        if (item.TargetVersion is not null)
        {
            args.Add("--version");
            args.Add(item.TargetVersion);
        }
        if (item.UnknownVersion)
        {
            args.Add("--include-unknown");
        }
        if (force)
        {
            args.Add("--force");
        }
        return args;
    }

    private static bool IsDashes(string line)
    {
        string t = line.Trim();
        return t.Length >= 10 && t.All(c => c == '-');
    }

    private static List<string> Cut(string line, int[] starts)
    {
        var cells = new List<string>();
        for (int k = 0; k < starts.Length; k++)
        {
            int from = starts[k];
            int to = k + 1 < starts.Length ? starts[k + 1] : line.Length;
            cells.Add(from >= line.Length ? "" : line[from..Math.Min(to, line.Length)].Trim());
        }
        return cells;
    }
}

public static class WingetSource
{
    public static async Task<SourceResult> CheckAsync(CancellationToken ct)
    {
        if (!Tools.HasWinget())
        {
            return new SourceResult
            {
                Source = UpdateSource.Apps,
                Failed = true,
                Message = "winget isn't on this PC. Install or update \"App Installer\" from the Microsoft Store, then check again.",
            };
        }
        ProcResult r = await ProcessRunner.RunAsync(
            Tools.Winget(),
            ["upgrade", "--include-unknown", "--accept-source-agreements", "--disable-interactivity"],
            TimeSpan.FromMinutes(4), ct: ct);
        List<WingetParser.Row> rows = WingetParser.ParseUpgradeTable(r.Output);
        if (rows.Count == 0 && r.ExitCode != 0 && !r.Output.Contains("No installed package", StringComparison.OrdinalIgnoreCase)
            && !r.Output.Contains("No available upgrade", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceResult { Source = UpdateSource.Apps, Failed = true, Message = "winget could not check: " + r.Tail(1).FirstOrDefault() };
        }

        var items = new List<UpdateItem>();
        // winget lists a package twice when it is installed in two scopes; one row per id keeps keys unique.
        foreach (WingetParser.Row row in rows.DistinctBy(r => r.Id, StringComparer.OrdinalIgnoreCase))
        {
            bool major = Versions.IsMajorJump(row.Version, row.Available);
            items.Add(new UpdateItem
            {
                Source = UpdateSource.Apps,
                Id = row.Id,
                Name = row.Name.TrimEnd('…').Trim(),
                Current = row.Version,
                Available = row.Available,
                UnknownVersion = Versions.IsUnknown(row.Version),
                IsMajor = major,
                Pinned = row.Pinned,
                NeedsAdmin = true,
            });
            if (major && Policy.IsRuntime(row.Id, row.Name))
            {
                ProcResult show = await ProcessRunner.RunAsync(
                    Tools.Winget(),
                    ["show", "--id", row.Id, "-e", "--versions", "--accept-source-agreements", "--disable-interactivity"],
                    TimeSpan.FromMinutes(2), ct: ct);
                string? same = WingetParser.HighestInFamily(WingetParser.ParseVersions(show.Output), row.Version);
                if (same is not null)
                {
                    items.Add(new UpdateItem
                    {
                        Source = UpdateSource.Apps,
                        Id = row.Id,
                        Name = $"{row.Name.TrimEnd('…').Trim()} (stay on {Versions.Family(row.Version)})",
                        Current = row.Version,
                        Available = same,
                        TargetVersion = same,
                        NeedsAdmin = true,
                    });
                }
            }
        }
        return new SourceResult
        {
            Source = UpdateSource.Apps,
            Items = items,
            Message = items.Count == 0 ? "All apps are up to date" : null,
        };
    }
}
