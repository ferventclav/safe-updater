using SafeUpdater.Core;

namespace SafeUpdater.Core.Tests;

public class VersionTests
{
    [Theory]
    [InlineData("24.15.0", "26.13.0", true)]
    [InlineData("3.14.7", "3.14.10", false)]
    [InlineData("0.13.1", "0.13.7", false)]
    [InlineData("0.13.1", "0.14.0", true)]
    [InlineData("18.0.0", "19.0.2", true)]
    [InlineData("2026.7.22", "2027.1.1", false)]
    [InlineData("2026.08.19", "2026.10.26", false)]
    [InlineData("< 175.0.0.13393", "175.0.0.13393", false)]
    [InlineData("N-125365-g9a01c1cb6a-20260630", "N-127043-g5a54fcf75e-20260930", false)]
    [InlineData("7.2.1 (48556)", "7.2.5 (51704)", false)]
    [InlineData("v1.2.3", "v2.0.0", true)]
    public void Major_jumps(string current, string available, bool major)
    {
        Assert.Equal(major, Versions.IsMajorJump(current, available));
    }

    [Theory]
    [InlineData("< 175.0", true)]
    [InlineData("Unknown", true)]
    [InlineData("", true)]
    [InlineData("1.0", false)]
    public void Unknown_installed_versions(string v, bool unknown)
    {
        Assert.Equal(unknown, Versions.IsUnknown(v));
    }

    [Theory]
    [InlineData("1.0.0-beta", true)]
    [InlineData("5.0-preview.3", true)]
    [InlineData("1.106.0-insider", true)]
    [InlineData("2.0.0rc1", true)]
    [InlineData("3.13.0a1", true)]
    [InlineData("2.0.0b2", true)]
    [InlineData("4.0.0.dev3", true)]
    [InlineData("1.0.0-rc.1", true)]
    [InlineData("26.05", false)]
    [InlineData("2.56.0.4", false)]
    [InlineData("7.2.5 (51704)", false)]
    [InlineData("1.0.2a", false)]
    [InlineData("1.2.3-build.5", false)]
    [InlineData("N-127043-g5a54fcf75e-20260930", false)]
    [InlineData("2026.10.26", false)]
    public void Pre_releases(string v, bool pre)
    {
        Assert.Equal(pre, Versions.IsPrerelease(v));
    }

    [Theory]
    [InlineData("2.0", "1.9", true)]
    [InlineData("1.9", "2.0", false)]
    [InlineData("1.0", "1.0.0", false)]
    [InlineData("3.14.10", "3.14.7", true)]
    [InlineData("< 175.0", "170.0", false)]
    [InlineData("N-127043-g5a54", "N-125365-g9a01", false)]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.1", false)]
    public void Downgrades(string current, string available, bool down)
    {
        Assert.Equal(down, Versions.IsDowngrade(current, available));
    }

    [Fact]
    public void Compare_and_family()
    {
        Assert.True(Versions.Compare("3.14.10", "3.14.7") > 0);
        Assert.Equal(0, Versions.Compare("1.0", "1.0.0"));
        Assert.Equal("24.x", Versions.Family("24.15.0"));
        Assert.Equal("0.13.x", Versions.Family("0.13.1"));
        Assert.True(Versions.SameFamily("24.22.1", "24.15.0"));
        Assert.False(Versions.SameFamily("26.13.0", "24.15.0"));
    }
}

/// <summary>
/// Every rule in Policy, one row each. A row says what the item is, what this PC is like, and what must happen:
/// hidden with a given label, or listed (ticked or not). Hidden items must always carry a reason and never be ticked.
/// </summary>
public class PolicyTests
{
    private const string Listed = "";

    public sealed record Case(Func<UpdateItem> Item, string Hidden, bool Ticked = false, PolicyContext? Ctx = null, bool ClaudeListed = false);

    private static PolicyContext Ctx(bool majors = false, string[]? ignored = null, string[]? choco = null, bool rustup = false) => new()
    {
        IncludeMajors = majors,
        Ignored = new HashSet<string>(ignored ?? [], StringComparer.OrdinalIgnoreCase),
        ChocolateyPackages = choco ?? [],
        RustupPresent = rustup,
    };

    private static UpdateItem App(string id, string cur, string avail, string? name = null, string? target = null, bool pinned = false,
        string? unsafeReason = null, UpdateSource source = UpdateSource.Apps) => new()
    {
        Source = source, Id = id, Name = name ?? id, Current = cur, Available = avail, TargetVersion = target, Pinned = pinned,
        UnknownVersion = Versions.IsUnknown(cur), IsMajor = target is null && Versions.IsMajorJump(cur, avail),
        NeedsAdmin = source is UpdateSource.Apps or UpdateSource.Chocolatey, UnsafeReason = unsafeReason,
    };

    private static UpdateItem Pip(string id, string cur, string avail, params string[] blockers) => new()
    {
        Source = UpdateSource.Python, Id = id, Name = id, Current = cur, Available = avail, Scope = "user",
        IsMajor = Versions.IsMajorJump(cur, avail), Blockers = [.. blockers],
    };

    private static UpdateItem Npm(string id, string cur, string avail) => new()
    {
        Source = UpdateSource.Node, Id = id, Name = id, Current = cur, Available = avail, IsMajor = Versions.IsMajorJump(cur, avail),
    };

    private static UpdateItem Driver(DateTime? offered, DateTime? installed, bool optional = false) => new()
    {
        Source = UpdateSource.Windows, Kind = ItemKind.Driver, Id = "6f1c0d2e-0000-4000-8000-000000000001",
        Name = "Intel - Display - 31.0.101.5000", DriverDate = offered, InstalledDriverDate = installed, Optional = optional, NeedsAdmin = true,
    };

    private static UpdateItem WinUpdate(bool optional) => new()
    {
        Source = UpdateSource.Windows, Kind = ItemKind.WindowsUpdate, Id = "6f1c0d2e-0000-4000-8000-000000000002",
        Name = "2026-11 Cumulative Update for Windows 11", Optional = optional, NeedsAdmin = true,
    };

    private static readonly DateTime Jan2025 = new(2025, 1, 10), Jul2026 = new(2026, 7, 21);

    public static readonly Dictionary<string, Case> Cases = new()
    {
        // ---- listed and ticked: routine updates
        ["routine app update"] = new(() => App("Git.Git", "2.55.0.5", "2.56.0.4"), Listed, Ticked: true),
        ["app major: apps handle their own upgrades"] = new(() => App("Obsidian.Obsidian", "1.14.4", "2.0.0"), Listed, Ticked: true),
        ["calendar versions are never majors"] = new(() => App("yt-dlp.yt-dlp", "2026.08.19", "2026.10.26"), Listed, Ticked: true),
        ["build-hash versions are not downgrades"] = new(() => App("Gyan.FFmpeg", "N-127043-g5a54", "N-125365-g9a01"), Listed, Ticked: true),
        ["pip minor"] = new(() => Pip("numpy", "2.5.1", "2.5.4"), Listed, Ticked: true),
        ["npm minor"] = new(() => Npm("docx", "9.8.0", "9.15.2"), Listed, Ticked: true),
        ["Chocolatey routine"] = new(() => App("git", "2.55.0", "2.56.0", source: UpdateSource.Chocolatey), Listed, Ticked: true),
        ["Scoop routine"] = new(() => App("7zip", "26.04", "26.05", source: UpdateSource.Scoop), Listed, Ticked: true),
        ["Windows cumulative update"] = new(() => WinUpdate(optional: false), Listed, Ticked: true),
        ["recommended driver newer than the installed one"] = new(() => Driver(Jul2026, Jan2025), Listed, Ticked: true),
        ["same-family Node entry while majors are off"] = new(() => App("OpenJS.NodeJS.LTS", "24.15.0", "24.22.1", target: "24.22.1"), Listed, Ticked: true),
        ["beta to newer beta: already on the beta track"] = new(() => App("Vendor.Tool", "2.0.0-beta.1", "2.0.0-beta.2"), Listed, Ticked: true),
        ["winget Rustup when rustup is not installed"] = new(() => App("Rustlang.Rustup", "1.29.1", "1.29.2"), Listed, Ticked: true),
        ["npm Claude Code when the Claude Code group is absent"] = new(() => Npm("@anthropic-ai/claude-code", "2.1.317", "2.1.318"), Listed, Ticked: true),
        ["GitHub CLI is not git's Chocolatey package"] = new(() => App("GitHub.cli", "2.80.0", "2.81.0", name: "GitHub CLI"), Listed, Ticked: true, Ctx(choco: ["git"])),
        ["the chocolatey package itself owns nothing"] = new(() => App("Chocolatey.Chocolatey", "2.4.1", "2.4.2", name: "Chocolatey"), Listed, Ticked: true, Ctx(choco: ["chocolatey"])),
        ["pip major with majors on"] = new(() => Pip("av", "18.0.0", "19.0.2"), Listed, Ticked: true, Ctx(majors: true)),
        ["npm major with majors on"] = new(() => Npm("rea-agents", "6.4.0", "7.0.0"), Listed, Ticked: true, Ctx(majors: true)),
        ["Node runtime major with majors on"] = new(() => App("OpenJS.NodeJS.LTS", "24.15.0", "26.13.0", name: "Node.js"), Listed, Ticked: true, Ctx(majors: true)),

        // ---- listed but left unticked: safe, your call
        ["unknown installed version"] = new(() => App("Ubisoft.Connect", "< 175.0.0.13393", "175.0.0.13393"), Listed),
        ["optional driver that is newer"] = new(() => Driver(Jul2026, Jan2025, optional: true), Listed),
        ["same-family entry once majors are on"] = new(() => App("OpenJS.NodeJS.LTS", "24.15.0", "24.22.1", target: "24.22.1"), Listed, Ctx: Ctx(majors: true)),
        ["NVIDIA driver is information only"] = new(() => new UpdateItem
        {
            Source = UpdateSource.Nvidia, Kind = ItemKind.Info, Id = "nvidia", Name = "GeForce Game Ready Driver", Current = "621.01", Available = "625.10", Selectable = false,
        }, Listed),

        // ---- 1. your choice
        ["hidden by you"] = new(() => App("Git.Git", "2.55.0.5", "2.56.0.4"), Policy.HiddenByYou, Ctx: Ctx(ignored: ["Apps|Git.Git"])),
        ["hiding is per source: choco git is still listed"] = new(() => App("git", "2.55.0", "2.56.0", source: UpdateSource.Chocolatey), Listed, Ticked: true, Ctx(ignored: ["Apps|Git.Git"])),
        // ---- 2. the source found it unsafe on this PC
        ["source says unsafe (conda, read-only, held)"] = new(() => App("numpy", "2.5.1", "2.5.4", unsafeReason: "Installed by conda.", source: UpdateSource.Python), "Not safe here"),
        // ---- 3. pinned
        ["winget pin"] = new(() => App("Vendor.Pinned", "1.0", "1.1", pinned: true), "Pinned"),
        ["Chocolatey pin"] = new(() => App("7zip.install", "26.04", "26.05", pinned: true, source: UpdateSource.Chocolatey), "Pinned"),
        // ---- 4. another tool owns it
        ["Chocolatey owns git"] = new(() => App("Git.Git", "2.55.0.5", "2.56.0.4", name: "Git"), "Managed by Chocolatey", Ctx: Ctx(choco: ["git"])),
        ["Chocolatey owns 7-Zip (.install package, versioned name)"] = new(() => App("7zip.7zip", "26.04", "26.05", name: "7-Zip 26.04 (x64 edition)"), "Managed by Chocolatey", Ctx: Ctx(choco: ["7zip.install"])),
        ["Chocolatey owns Chrome (whole id)"] = new(() => App("Google.Chrome", "142.0.1", "142.0.2", name: "Google Chrome"), "Managed by Chocolatey", Ctx: Ctx(choco: ["googlechrome"])),
        ["Chocolatey owns VS Code (known pair)"] = new(() => App("Microsoft.VisualStudioCode", "1.105.0", "1.106.0", name: "Microsoft Visual Studio Code (User)"), "Managed by Chocolatey", Ctx: Ctx(choco: ["vscode"])),
        ["Chocolatey owns Python (name plus version)"] = new(() => App("Python.Python.3.14", "3.14.9", "3.14.10", name: "Python 3.14.9 (64-bit)"), "Managed by Chocolatey", Ctx: Ctx(choco: ["python"])),
        ["rustup handles its own winget record"] = new(() => App("Rustlang.Rustup", "1.29.1", "1.29.2"), "Handled by rustup", Ctx: Ctx(rustup: true)),
        ["Claude Code is not updated twice"] = new(() => Npm("@anthropic-ai/claude-code", "2.1.317", "2.1.318"), "Handled by Claude Code", ClaudeListed: true),
        // ---- 5. never go backwards
        ["app downgrade"] = new(() => App("Vendor.Tool", "2.0.1", "1.9.8"), "Older version"),
        ["pip downgrade"] = new(() => Pip("tokenizers", "0.22.1", "0.22.0"), "Older version"),
        // ---- 6. never move a stable install onto a test release
        ["stable app offered a beta"] = new(() => App("Vendor.Tool", "1.4.0", "2.0.0-beta.1"), "Pre-release"),
        ["stable pip offered an rc"] = new(() => Pip("numpy", "2.5.1", "2.6.0rc1"), "Pre-release", Ctx: Ctx(majors: true)),
        // ---- 7. another installed package needs the current version
        ["pip blocker"] = new(() => Pip("phonemizer-fork", "3.3.1", "3.3.2", "kokoro-onnx needs phonemizer-fork==3.3.1"), "Would break another package"),
        ["pip blocker stays hidden with majors on"] = new(() => Pip("phonemizer-fork", "3.3.1", "3.3.2", "kokoro-onnx needs phonemizer-fork==3.3.1"), "Would break another package", Ctx: Ctx(majors: true)),
        // ---- 8. Windows Update
        ["optional Windows update (preview)"] = new(() => WinUpdate(optional: true), "Optional"),
        ["driver with no date"] = new(() => Driver(null, Jan2025), "Can't confirm"),
        ["driver whose device wasn't found"] = new(() => Driver(Jul2026, null), "Can't confirm"),
        ["driver older than the installed one (the 2017 Intel case)"] = new(() => Driver(new DateTime(2017, 9, 19), Jan2025), "Not newer"),
        ["driver the same age as the installed one"] = new(() => Driver(Jan2025, Jan2025), "Not newer"),
        ["older driver stays hidden with majors on"] = new(() => Driver(new DateTime(2017, 9, 19), Jan2025), "Not newer", Ctx: Ctx(majors: true)),
        ["optional older driver is hidden, not just unticked"] = new(() => Driver(new DateTime(2017, 9, 19), Jan2025, optional: true), "Not newer"),
        // ---- 9. majors of libraries and runtimes
        ["pip major (the av 19 case)"] = new(() => Pip("av", "18.0.0", "19.0.2"), "Major version"),
        ["pip 0.x minor counts as major"] = new(() => Pip("tokenizers", "0.21.4", "0.22.1"), "Major version"),
        ["npm major"] = new(() => Npm("rea-agents", "6.4.0", "7.0.0"), "Major version"),
        ["Node runtime major in winget"] = new(() => App("OpenJS.NodeJS.LTS", "24.15.0", "26.13.0", name: "Node.js"), "Major version"),
        ["PostgreSQL major in winget"] = new(() => App("PostgreSQL.PostgreSQL.17", "17.6", "18.1", name: "PostgreSQL 17"), "Major version"),
        ["Temurin JDK major in winget"] = new(() => App("EclipseAdoptium.Temurin.21.JDK", "21.0.8", "25.0.1", name: "Eclipse Temurin JDK"), "Major version"),
        ["Node runtime major in Chocolatey"] = new(() => App("nodejs", "24.15.0", "26.13.0", source: UpdateSource.Chocolatey), "Major version"),
        ["Python runtime major in Scoop"] = new(() => App("python", "3.14.10", "4.0.0", source: UpdateSource.Scoop), "Major version"),

        // ---- precedence: the first matching rule names the reason
        ["ignored beats downgrade"] = new(() => App("Vendor.Tool", "2.0.1", "1.9.8"), Policy.HiddenByYou, Ctx: Ctx(ignored: ["Apps|Vendor.Tool"])),
        ["unsafe beats pinned"] = new(() => App("Vendor.Tool", "1.0", "1.1", pinned: true, unsafeReason: "Held in Scoop."), "Not safe here"),
        ["pinned beats major"] = new(() => App("nodejs", "24.15.0", "26.13.0", pinned: true, source: UpdateSource.Chocolatey), "Pinned"),
        ["Chocolatey owner beats runtime major"] = new(() => App("OpenJS.NodeJS.LTS", "24.15.0", "26.13.0", name: "Node.js"), "Managed by Chocolatey", Ctx: Ctx(choco: ["nodejs-lts"])),
        ["pre-release beats major"] = new(() => Pip("numpy", "2.5.1", "3.0.0b1"), "Pre-release"),
    };

    public static TheoryData<string> CaseNames => new(Cases.Keys);

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void Rule(string name)
    {
        Case c = Cases[name];
        UpdateItem i = c.Item();

        Policy.Apply(i, c.Ctx ?? Ctx(), c.ClaudeListed);

        Assert.Equal(c.Hidden, i.Hidden ? i.Badge : Listed);
        Assert.Equal(c.Ticked, i.Selected);
        Assert.Equal(i.Selected, i.Recommended);
        if (i.Hidden)
        {
            Assert.False(string.IsNullOrWhiteSpace(i.Note));
            Assert.False(i.Selected);
        }
    }

    [Fact]
    public void Every_rule_has_a_row()
    {
        // Every label HideReason can return, read from its source, so a new rule without a row here fails this test.
        string source = File.ReadAllText(Path.Combine(RepoRoot(), "src", "SafeUpdater.Core", "Policy.cs"));
        List<string> labels = System.Text.RegularExpressions.Regex.Matches(source, @"return \(""([^""]+)""")
            .Select(m => m.Groups[1].Value)
            .Append(Policy.HiddenByYou)
            .Distinct()
            .ToList();

        Assert.Equal(13, labels.Count);
        Assert.All(labels, l => Assert.Contains(Cases.Values, c => c.Hidden == l));
    }

    private static string RepoRoot()
    {
        string? d = AppContext.BaseDirectory;
        while (d is not null && !File.Exists(Path.Combine(d, "global.json")))
        {
            d = Path.GetDirectoryName(d);
        }
        return d ?? throw new DirectoryNotFoundException("global.json not found above the test folder");
    }

    [Fact]
    public void Apply_all_sees_across_groups()
    {
        UpdateItem npmClaude = Npm("@anthropic-ai/claude-code", "2.1.317", "2.1.318");
        UpdateItem claude = new() { Source = UpdateSource.ClaudeCode, Id = "claude", Name = "Claude Code", Current = "2.1.317", Available = "2.1.318" };
        UpdateItem git = App("Git.Git", "2.55.0.5", "2.56.0.4", name: "Git");

        Policy.ApplyAll([npmClaude, claude, git], Ctx(choco: ["git"]));

        Assert.Equal("Handled by Claude Code", npmClaude.Badge);
        Assert.True(claude.Selected);
        Assert.Equal("Managed by Chocolatey", git.Badge);
        Assert.Contains("git", git.Note);
    }

    [Fact]
    public void Rules_rerun_cleanly_when_settings_change()
    {
        UpdateItem i = Pip("av", "18.0.0", "19.0.2");

        Policy.Apply(i, Ctx(ignored: ["Python|av"]));
        Assert.True(i.IgnoredByUser);
        Policy.Apply(i, Ctx());
        Assert.False(i.IgnoredByUser);
        Assert.Equal("Major version", i.Badge);
        Policy.Apply(i, Ctx(majors: true));
        Assert.False(i.Hidden);
        Assert.True(i.Selected);
        Assert.Contains("rolled back", i.Note);
    }

    [Fact]
    public void Ignoring_remembers_the_package_not_the_version()
    {
        UpdateItem a = App("OpenJS.NodeJS.LTS", "24.15.0", "26.13.0");
        UpdateItem b = App("OpenJS.NodeJS.LTS", "24.15.0", "24.22.1", target: "24.22.1");

        Assert.NotEqual(a.Key, b.Key);
        Assert.Equal(a.IgnoreKey, b.IgnoreKey);
    }

    [Fact]
    public void Whisper_packages_carry_a_note()
    {
        UpdateItem numpy = Pip("numpy", "2.5.1", "2.5.4");
        UpdateItem hub = Pip("huggingface_hub", "1.33.0", "1.34.0");

        Policy.Apply(numpy, Ctx());
        Policy.Apply(hub, Ctx());

        Assert.Contains("Whisper", numpy.Note);
        Assert.Contains("Whisper", hub.Note);
    }

    [Fact]
    public void Driver_notes_name_the_installed_date()
    {
        UpdateItem d = Driver(Jul2026, Jan2025);
        UpdateItem old = Driver(new DateTime(2017, 9, 19), Jan2025);

        Policy.Apply(d, Ctx());
        Policy.Apply(old, Ctx());

        // Dates follow the PC's own format ("Sept" on English (India)), so the expected text is built the same way.
        string jan = $"{Jan2025:d MMM yyyy}", sep = $"{new DateTime(2017, 9, 19):d MMM yyyy}";
        Assert.Contains(jan, d.Note);
        Assert.Contains(sep, old.Note);
        Assert.Contains(jan, old.Note);
    }

    [Fact]
    public void Running_apps_are_matched_by_name_without_false_alarms()
    {
        UpdateItem[] items =
        [
            App("Obsidian.Obsidian", "1.14.4", "1.15.0"),
            App("TheDocumentFoundation.LibreOffice", "26.8.1.1", "26.8.2.2", name: "LibreOffice 26.8.1.1"),
            App("Microsoft.DotNet.Runtime.8", "8.0.32", "8.0.33", name: "Microsoft .NET Runtime - 8.0.32 (x64)"),
            App("Nvidia.GeForceNow", "2.0.88", "2.0.89", name: "NVIDIA GeForce NOW 2.0.88.129"),
        ];
        string[] running = ["Obsidian", "soffice", "MicrosoftEdge", "NVIDIA Overlay", "explorer"];

        List<string> open = RunningApps.Find(items, running);

        Assert.Equal(["Obsidian.Obsidian", "LibreOffice 26.8.1.1"], open);
    }

    [Fact]
    public void Commands_are_described_exactly()
    {
        UpdateItem node = App("OpenJS.NodeJS.LTS", "24.15.0", "24.22.1", target: "24.22.1");
        UpdateItem pipUser = Pip("numpy", "2.5.1", "2.5.4");
        var pipEnv = new UpdateItem { Source = UpdateSource.Python, Id = "numpy", Name = "numpy", Available = "2.5.4", Scope = "env" };
        UpdateItem choco = App("git", "2.55.0", "2.56.0", source: UpdateSource.Chocolatey);
        UpdateItem scoop = App("7zip", "26.04", "26.05", source: UpdateSource.Scoop);

        Assert.StartsWith("winget upgrade --id OpenJS.NodeJS.LTS -e", Commands.Describe(node));
        Assert.Contains("--version 24.22.1", Commands.Describe(node));
        Assert.Equal("python -m pip install --user --upgrade numpy==2.5.4", Commands.Describe(pipUser));
        Assert.Equal("python -m pip install --upgrade numpy==2.5.4", Commands.Describe(pipEnv));
        Assert.Equal("choco upgrade git -y --no-progress -r", Commands.Describe(choco));
        Assert.Equal("scoop update 7zip", Commands.Describe(scoop));
    }
}
