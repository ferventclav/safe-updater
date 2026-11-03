namespace SafeUpdater.Core;

/// <summary>The groups shown in the window, in display order.</summary>
public enum UpdateSource { Apps, Chocolatey, Scoop, Windows, Nvidia, Python, Node, Rust, ClaudeCode }

public enum ItemKind { Package, WindowsUpdate, Driver, StoreScan, Info }

public enum ItemStatus { Idle, Queued, Running, Done, Failed, Skipped }

/// <summary>One thing that can be updated: a winget app, a Windows update, a pip package and so on.</summary>
public sealed class UpdateItem
{
    public required UpdateSource Source { get; init; }
    public ItemKind Kind { get; init; } = ItemKind.Package;

    /// <summary>winget or Chocolatey id, pip or npm name, Windows Update id, rustup toolchain.</summary>
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Current { get; init; } = "";
    public string Available { get; init; } = "";

    /// <summary>An exact version to install instead of the newest (the "stay on 24.x" Node entry).</summary>
    public string? TargetVersion { get; init; }

    public bool UnknownVersion { get; init; }
    public bool IsMajor { get; init; }
    public bool Pinned { get; init; }
    public bool NeedsAdmin { get; init; }
    public bool Selectable { get; init; } = true;
    public DateTime? DriverDate { get; init; }

    /// <summary>For a Windows driver: the date of the driver already on that device, when it could be found.</summary>
    public DateTime? InstalledDriverDate { get; init; }

    /// <summary>For a Windows update: optional (previews, extras) rather than one Windows would install itself.</summary>
    public bool Optional { get; init; }
    public string? Detail { get; init; }

    /// <summary>pip: "user" for the user site, "env" for the interpreter's own site-packages.</summary>
    public string? Scope { get; init; }

    /// <summary>Other installed packages whose version rules exclude the newest version.</summary>
    public List<string> Blockers { get; init; } = [];

    /// <summary>Set by a source that knows updating would be unsafe on this PC (needs admin, conda, held in Scoop).</summary>
    public string? UnsafeReason { get; set; }

    public bool Recommended { get; set; }
    public bool Selected { get; set; }

    /// <summary>Unsafe to update: left out of the list (so it can't be ticked by accident), shown only with its reason.</summary>
    public bool Hidden { get; set; }

    /// <summary>Hidden because the user asked not to see it again.</summary>
    public bool IgnoredByUser { get; set; }
    public string? Badge { get; set; }
    public string? Note { get; set; }

    public ItemStatus Status { get; set; }
    public string? Result { get; set; }
    public bool RebootNeeded { get; set; }

    /// <summary>Set by the admin worker when the item should be tried again as the signed-in user.</summary>
    public bool RetryLocally { get; set; }
    public List<string> Output { get; } = [];

    public string Key => TargetVersion is null ? $"{Source}|{Id}" : $"{Source}|{Id}|{TargetVersion}";

    /// <summary>What "Don't show this update" remembers: the package, not the version.</summary>
    public string IgnoreKey => $"{Source}|{Id}";
}

public sealed class SourceResult
{
    public required UpdateSource Source { get; init; }
    public List<UpdateItem> Items { get; init; } = [];

    /// <summary>A line for the group header: "Driver 621.01 is the newest", "Python was not found".</summary>
    public string? Message { get; init; }
    public bool Failed { get; init; }

    /// <summary>The tool isn't on this PC, so the group is not shown at all.</summary>
    public bool Absent { get; init; }

    /// <summary>Chocolatey: every package it manages, so winget doesn't update the same apps behind its back.</summary>
    public List<string> Managed { get; init; } = [];
}

public sealed record SourceCheck(UpdateSource Source, Func<CancellationToken, Task<SourceResult>> Check);

public sealed class RunSummary
{
    public int Updated { get; set; }
    public int Failed { get; set; }
    public int Skipped { get; set; }
    public bool RebootRecommended { get; set; }
    public List<string> Notes { get; } = [];
    public string LogFile { get; set; } = "";
}
