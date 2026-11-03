using System.Text.RegularExpressions;

namespace SafeUpdater.Core;

/// <summary>What the rules need to know besides the item itself.</summary>
public sealed class PolicyContext
{
    public bool IncludeMajors { get; init; }
    public IReadOnlySet<string> Ignored { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public DateTime Today { get; init; } = DateTime.Today;

    /// <summary>Packages Chocolatey manages; winget must not update the same apps.</summary>
    public IReadOnlyCollection<string> ChocolateyPackages { get; init; } = [];

    /// <summary>rustup is on this PC, so winget's own Rustup entry is redundant.</summary>
    public bool RustupPresent { get; init; }

    public static PolicyContext From(Settings s, IReadOnlyCollection<string> chocolatey, bool rustup) => new()
    {
        IncludeMajors = s.IncludeMajors,
        Ignored = s.Ignored,
        ChocolateyPackages = chocolatey,
        RustupPresent = rustup,
    };
}

/// <summary>
/// Decides, for every update, one of three things:
/// HIDDEN (unsafe or redundant: not listed, so it can't be ticked, and shown only with its reason),
/// LISTED BUT UNTICKED (safe, but your call), or LISTED AND TICKED (routine).
/// The hide rules run in a fixed order and the first match gives the reason; see README for the table.
/// </summary>
public static partial class Policy
{
    public const string HiddenByYou = "Hidden by you";

    /// <summary>Runtimes and database servers: a major version can break projects, global packages or data files.</summary>
    private static readonly string[] RuntimeWords =
        ["nodejs", "python", "openjdk", "temurin", "corretto", "zulu", "jdk", "postgresql", "mysql", "mariadb", "mongodb", "ruby", "php", "erlang", "elixir", "golang"];

    /// <summary>Packages GPU Whisper depends on. They are tested after updating; nothing more.</summary>
    public static readonly string[] WhisperStack =
        ["faster-whisper", "ctranslate2", "av", "onnxruntime", "tokenizers", "huggingface-hub", "numpy"];

    private const string MajorsHint = " Turn on Include major versions to list it.";

    /// <summary>A driver older than this is hidden whatever the device has (the 2017 Intel chipset driver Windows keeps offering).</summary>
    private const int OldDriverYears = 3;

    private static readonly DateTime InboxDriverDate = new(2006, 6, 21);

    [GeneratedRegex(@"[^a-z0-9]")]
    private static partial Regex NonAlnum();

    private static string Squash(string s) => NonAlnum().Replace(s.ToLowerInvariant(), "");

    /// <summary>A language runtime or database server, judged by its id or name, from any app source.</summary>
    public static bool IsRuntime(string id, string name)
    {
        string a = Squash(id), b = Squash(name);
        return RuntimeWords.Any(w => a.Contains(w) || b.Contains(w));
    }

    public static bool IsRuntime(UpdateItem i) =>
        i.Source is UpdateSource.Apps or UpdateSource.Chocolatey or UpdateSource.Scoop && IsRuntime(i.Id, i.Name);

    /// <summary>Applies the rules to every item; some rules look across groups (Chocolatey owning a winget app).</summary>
    public static void ApplyAll(IReadOnlyList<UpdateItem> items, PolicyContext ctx)
    {
        bool claudeListed = items.Any(i => i.Source == UpdateSource.ClaudeCode);
        foreach (UpdateItem i in items)
        {
            Apply(i, ctx, claudeListed);
        }
    }

    public static void Apply(UpdateItem item, PolicyContext ctx, bool claudeCodeListed = false)
    {
        item.Badge = null;
        item.Note = null;
        item.Hidden = false;
        item.IgnoredByUser = false;

        if (HideReason(item, ctx, claudeCodeListed) is (string label, string reason))
        {
            item.Hidden = true;
            item.IgnoredByUser = label == HiddenByYou;
            item.Badge = label;
            item.Note = reason;
            item.Recommended = false;
            item.Selected = false;
            return;
        }

        bool tick = true;
        if (item.Source == UpdateSource.Nvidia)
        {
            tick = false;
            item.Badge = "In NVIDIA app";
            item.Note = "Install it from the NVIDIA app. Graphics drivers can't be installed from here.";
        }
        else if (item.TargetVersion is not null)
        {
            tick = !ctx.IncludeMajors;
            item.Badge = "Same family";
            item.Note = $"The newest {Versions.Family(item.Current)} release, listed instead of the new major version.";
        }
        else if (item.IsMajor)
        {
            item.Badge = "Major";
            item.Note = item.Source is UpdateSource.Python or UpdateSource.Node || IsRuntime(item)
                ? "A new major version, listed because you turned on Include major versions."
                  + (item.Source == UpdateSource.Python ? " Your tools are tested afterwards, and it is rolled back if a test that passed before now fails." : "")
                : "A new major version of an app. Apps handle their own upgrades, so it is picked.";
        }
        else if (item.UnknownVersion)
        {
            tick = false;
            item.Badge = "Unknown version";
            item.Note = "winget can't read the installed version, so this reinstalls the newest one. Safe, but apps like this usually update themselves when you open them.";
        }

        if (item.Kind == ItemKind.Driver)
        {
            item.Badge = "Driver";
            string installed = item.InstalledDriverDate is DateTime inst ? $" (yours is from {inst:d MMM yyyy})" : "";
            if (item.Optional)
            {
                tick = false;
                item.Note = $"Newer than the driver you have{installed}, but optional. Windows only suggests optional drivers when something isn't working.";
            }
            else
            {
                item.Note = $"A driver update Windows recommends. Newer than the one you have{installed}.";
            }
        }

        if (item.Source == UpdateSource.Python && item.Note is null
            && WhisperStack.Contains(PythonSource.Normalize(item.Id), StringComparer.OrdinalIgnoreCase))
        {
            item.Note = "GPU Whisper uses this. It is tested after updating and rolled back if Whisper stops working.";
        }

        item.Recommended = tick && item.Selectable;
        item.Selected = item.Recommended;
    }

    /// <summary>The first rule that makes an update unsafe or redundant, as (short label, reason), or null.</summary>
    public static (string Label, string Reason)? HideReason(UpdateItem item, PolicyContext ctx, bool claudeCodeListed = false)
    {
        // 1. Your choice.
        if (ctx.Ignored.Contains(item.IgnoreKey))
        {
            return (HiddenByYou, "You chose not to see this update. Use Show again to bring it back.");
        }
        // 2. The source found it can't be done safely on this PC (needs admin, conda-managed, held, failed install).
        if (item.UnsafeReason is not null)
        {
            return ("Not safe here", item.UnsafeReason);
        }
        // 3. Someone pinned it on purpose.
        if (item.Pinned)
        {
            return ("Pinned", item.Source == UpdateSource.Chocolatey
                ? "Pinned in Chocolatey (choco pin), so it stays at this version until the pin is removed."
                : "Pinned in winget, so it only updates when you ask winget for it.");
        }
        // 4. Another tool owns it; updating it here too would update it twice or confuse that tool.
        if (item.Source == UpdateSource.Apps && ChocolateySource.OwnerOf(item, ctx.ChocolateyPackages) is string owner)
        {
            return ("Managed by Chocolatey", $"Chocolatey manages this app (package {owner}). Updating it with winget as well leaves Chocolatey's records wrong, so it updates in the Chocolatey group.");
        }
        if (item.Source == UpdateSource.Apps && ctx.RustupPresent && item.Id.Equals("Rustlang.Rustup", StringComparison.OrdinalIgnoreCase))
        {
            return ("Handled by rustup", "rustup updates itself in the Rust group. winget's record of it is out of date, and updating it through winget could clash with rustup.");
        }
        if (item.Source == UpdateSource.Node && claudeCodeListed && item.Id.Equals("@anthropic-ai/claude-code", StringComparison.OrdinalIgnoreCase))
        {
            return ("Handled by Claude Code", "This is Claude Code, which updates in its own group, so it isn't installed twice.");
        }
        // 5. Never go backwards.
        if (Versions.IsDowngrade(item.Current, item.Available))
        {
            return ("Older version", $"{item.Available} is older than the {item.Current} you have, so installing it would downgrade.");
        }
        // 6. Never move a stable install onto a test release.
        if (Versions.IsPrerelease(item.Available) && !Versions.IsPrerelease(item.Current))
        {
            return ("Pre-release", $"{item.Available} is a test release (alpha, beta, release candidate or preview), and you have a stable version.");
        }
        // 7. Another installed package needs the current version.
        if (item.Blockers.Count > 0)
        {
            return ("Would break another package", string.Join("; ", item.Blockers) + ". Updating it would break that package, so it waits until that package allows the new version.");
        }
        // 8. Windows Update: optional extras, and drivers that aren't clearly newer than the one on the device.
        if (item.Source == UpdateSource.Windows)
        {
            if (item.Kind == ItemKind.WindowsUpdate && item.Optional)
            {
                return ("Optional", "An optional update, such as a preview. Windows installs the important updates by itself, and these fixes arrive in a later regular update.");
            }
            if (item.Kind == ItemKind.Driver)
            {
                if (item.DriverDate is not DateTime offered)
                {
                    return ("Can't confirm", "Windows Update didn't say how new this driver is, so it can't be checked against the one you have.");
                }
                if (offered < ctx.Today.AddYears(-OldDriverYears))
                {
                    return ("Old driver", $"This driver is from {offered:d MMM yyyy}. Drivers this old are almost always older than what Windows already uses for the device, so it is left out.");
                }
                // Windows' own in-box drivers all carry the date 21 Jun 2006, which says nothing about how new they are.
                if (item.InstalledDriverDate is not DateTime installed || installed.Date == InboxDriverDate)
                {
                    return ("Can't confirm", $"This driver is dated {offered:d MMM yyyy}, but "
                        + (item.InstalledDriverDate is null ? "the device it's for wasn't found" : "the device uses Windows' own driver, whose 21 Jun 2006 date says nothing about its age")
                        + ", so it can't be confirmed as newer than the driver you have.");
                }
                if (offered <= installed)
                {
                    return ("Not newer", $"This driver is dated {offered:d MMM yyyy}. The one already on the device is from {installed:d MMM yyyy}, so installing it would replace a newer or equal driver.");
                }
            }
        }
        // 9. Major versions of libraries and runtimes wait for your OK.
        if (item.IsMajor && !ctx.IncludeMajors)
        {
            if (item.Source is UpdateSource.Python or UpdateSource.Node)
            {
                return ("Major version", "A new major version of a library. It can change how your scripts and tools work (av 19 broke faster-whisper this way)." + MajorsHint);
            }
            if (IsRuntime(item))
            {
                return ("Major version", "A new major version of a runtime or database. Projects, global packages or data files can need changes to work with it."
                    + (item.Source == UpdateSource.Apps ? " When there is a newer release in your current family, that is listed instead." : "") + MajorsHint);
            }
        }
        return null;
    }
}
