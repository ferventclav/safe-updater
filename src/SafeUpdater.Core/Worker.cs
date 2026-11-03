using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;

namespace SafeUpdater.Core;

/// <summary>What the window hands to the elevated helper: the admin items plus the PATH snapshot.</summary>
public sealed class WorkerJob
{
    public string Stamp { get; set; } = "";
    public List<JobItem> Items { get; set; } = [];
    public PathHealth.Snapshot? Path { get; set; }
    public string ProgressFile { get; set; } = "";
    public string LogFile { get; set; } = "";

    /// <summary>The account that pressed Update. If admin was approved with another account, per-user work is sent back.</summary>
    public string? RequestingUserSid { get; set; }
    public bool RestorePoint { get; set; }
}

public sealed class JobItem
{
    public string Key { get; set; } = "";
    public UpdateSource Source { get; set; }
    public ItemKind Kind { get; set; }
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? TargetVersion { get; set; }
    public bool UnknownVersion { get; set; }

    public static JobItem From(UpdateItem i) => new()
    {
        Key = i.Key, Source = i.Source, Kind = i.Kind, Id = i.Id, Name = i.Name, TargetVersion = i.TargetVersion, UnknownVersion = i.UnknownVersion,
    };

    internal UpdateItem ToItem() => new()
    {
        Source = Source, Kind = Kind, Id = Id, Name = Name, TargetVersion = TargetVersion, UnknownVersion = UnknownVersion,
    };
}

/// <summary>One line of the progress file (JSON lines) the helper writes and the window follows.</summary>
public sealed class ProgressEvent
{
    public string? Key { get; set; }
    public ItemStatus Status { get; set; }
    public string? Message { get; set; }
    public bool Reboot { get; set; }
    public bool RetryLocally { get; set; }
    public List<string>? Output { get; set; }
    public bool Finished { get; set; }
}

/// <summary>The elevated half: "SafeUpdater.exe --worker job.json". It never shows a window.</summary>
public static class Worker
{
    public static int Run(string jobPath)
    {
        try
        {
            WorkerJob job = JsonSerializer.Deserialize<WorkerJob>(File.ReadAllText(jobPath), Json.Options)
                ?? throw new InvalidDataException("Empty job file");
            RunAsync(job).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception ex)
        {
            RunLog.WriteError("Worker failed: " + ex);
            return 1;
        }
    }

    public static async Task RunAsync(WorkerJob job)
    {
        void Emit(ProgressEvent e) =>
            RunLog.Append(job.ProgressFile, JsonSerializer.Serialize(e, Json.Options));

        // When a standard user types an admin's password, this process is that admin: its HKCU, AppData and
        // per-user installs belong to the wrong person. Machine-wide work is still fine; per-user work goes back.
        bool sameUser = job.RequestingUserSid is null
            || string.Equals(WindowsIdentity.GetCurrent().User?.Value, job.RequestingUserSid, StringComparison.OrdinalIgnoreCase);

        if (job.RestorePoint && job.Items.Count > 0)
        {
            Emit(new ProgressEvent { Key = "status", Message = "Making a restore point…" });
            ProcResult rp = await ProcessRunner.PowerShellAsync(Preflight.RestorePointScript, TimeSpan.FromMinutes(5));
            RunLog.Append(job.LogFile, "=== Restore point\n" + rp.Output);
            Emit(new ProgressEvent { Key = "restore-point", Status = ItemStatus.Done, Message = Preflight.DescribeRestorePoint(rp.Output) });
        }

        // Runtimes first so later installers see them; Visual Studio last because it is slow.
        IEnumerable<JobItem> apps = job.Items
            .Where(i => i.Kind == ItemKind.Package && i.Source == UpdateSource.Apps)
            .OrderBy(i => i.Id.StartsWith("Python.", StringComparison.OrdinalIgnoreCase) || i.Id.StartsWith("OpenJS.", StringComparison.OrdinalIgnoreCase) ? 0
                : i.Id.StartsWith("Microsoft.VisualStudio", StringComparison.OrdinalIgnoreCase) ? 2 : 1);

        foreach (JobItem ji in apps)
        {
            if (!sameUser)
            {
                Emit(new ProgressEvent
                {
                    Key = ji.Key, Status = ItemStatus.Queued, RetryLocally = true,
                    Message = "Admin was approved with a different account, so this installs as you instead.",
                });
                continue;
            }
            UpdateItem item = ji.ToItem();
            Emit(new ProgressEvent { Key = ji.Key, Status = ItemStatus.Running, Message = "Installing…" });
            RunLog.Append(job.LogFile, $"=== {ji.Name} [{ji.Id}] (admin)");
            ProcResult r = await ProcessRunner.RunAsync(Tools.Winget(), WingetParser.UpgradeArgs(item, force: false), TimeSpan.FromMinutes(45));
            RunLog.Append(job.LogFile, r.Output + $"exit {r.ExitCode}");
            WingetOutcome outcome = WingetParser.Classify(r.ExitCode, r.Output, elevated: true);
            if (outcome == WingetOutcome.RetryForce)
            {
                RunLog.Append(job.LogFile, "--- retrying with --force");
                r = await ProcessRunner.RunAsync(Tools.Winget(), WingetParser.UpgradeArgs(item, force: true), TimeSpan.FromMinutes(45));
                RunLog.Append(job.LogFile, r.Output + $"exit {r.ExitCode}");
                outcome = WingetParser.Classify(r.ExitCode, r.Output, elevated: true);
            }
            Emit(outcome switch
            {
                WingetOutcome.Ok => Done(ji, "Updated", r),
                WingetOutcome.OkReboot => Done(ji, "Updated. Restart to finish.", r, reboot: true),
                WingetOutcome.AlreadyCurrent => Done(ji, "Already up to date", r),
                WingetOutcome.RetryNonAdmin => new ProgressEvent
                {
                    Key = ji.Key, Status = ItemStatus.Queued, RetryLocally = true, Output = r.Tail(12).ToList(),
                    Message = "Its installer failed as admin. Trying again without admin…",
                },
                WingetOutcome.InUse => Fail(ji, "A file was in use. Close the app and try again.", r),
                _ => Fail(ji, "Didn't install: " + (r.Tail(1).FirstOrDefault() ?? $"exit {r.ExitCode}"), r),
            });
        }

        string? choco = Tools.Choco();
        foreach (JobItem ji in job.Items.Where(i => i.Source == UpdateSource.Chocolatey))
        {
            if (choco is null)
            {
                Emit(new ProgressEvent { Key = ji.Key, Status = ItemStatus.Failed, Message = "Chocolatey was not found" });
                continue;
            }
            Emit(new ProgressEvent { Key = ji.Key, Status = ItemStatus.Running, Message = "Installing…" });
            RunLog.Append(job.LogFile, $"=== {ji.Name} (Chocolatey, admin)");
            ProcResult r = await ProcessRunner.RunAsync(choco, ChocolateySource.UpgradeArgs(ji.ToItem()), TimeSpan.FromMinutes(45));
            RunLog.Append(job.LogFile, r.Output + $"exit {r.ExitCode}");
            // 1641 and 3010 are the standard "succeeded, restart needed" installer codes.
            Emit(r.ExitCode switch
            {
                0 => Done(ji, "Updated", r),
                1641 or 3010 => Done(ji, "Updated. Restart to finish.", r, reboot: true),
                _ => Fail(ji, "Didn't install: " + (r.Tail(1).FirstOrDefault() ?? $"exit {r.ExitCode}"), r),
            });
        }

        List<JobItem> wu = job.Items.Where(i => i.Kind is ItemKind.WindowsUpdate or ItemKind.Driver).ToList();
        if (wu.Count > 0)
        {
            foreach (JobItem ji in wu)
            {
                Emit(new ProgressEvent { Key = ji.Key, Status = ItemStatus.Running, Message = "Downloading and installing…" });
            }
            RunLog.Append(job.LogFile, "=== Windows Update: " + string.Join(", ", wu.Select(w => w.Name)));
            ProcResult r = await ProcessRunner.PowerShellAsync(WindowsUpdateSource.InstallScript(wu.Select(w => w.Id)), TimeSpan.FromMinutes(90));
            RunLog.Append(job.LogFile, r.Output);
            List<WindowsUpdateSource.InstallResult> results = WindowsUpdateSource.ParseInstall(r.Output);
            foreach (JobItem ji in wu)
            {
                WindowsUpdateSource.InstallResult? res = results.FirstOrDefault(x => x.Id.Equals(ji.Id, StringComparison.OrdinalIgnoreCase));
                Emit(res is null
                    ? Fail(ji, "Windows Update didn't install it (no longer offered, or the install failed).", r)
                    : res.Succeeded
                        ? Done(ji, res.Reboot ? "Installed. Restart to finish." : "Installed", r, res.Reboot)
                        : Fail(ji, $"Windows Update reported result code {res.ResultCode}", r));
            }
        }

        JobItem? store = job.Items.FirstOrDefault(i => i.Kind == ItemKind.StoreScan);
        if (store is not null)
        {
            Emit(new ProgressEvent { Key = store.Key, Status = ItemStatus.Running, Message = "Asking the Store…" });
            ProcResult r = await ProcessRunner.PowerShellAsync(WindowsUpdateSource.StoreScanScript, TimeSpan.FromMinutes(5));
            RunLog.Append(job.LogFile, "=== Microsoft Store scan\n" + r.Output);
            Emit(r.Output.Contains("ReturnValue=0")
                ? Done(store, "The Store is updating your apps in the background", r)
                : Fail(store, "The Store didn't accept the request", r));
        }

        if (job.Path is not null && sameUser)
        {
            List<PathHealth.Repair> plan = PathHealth.Plan(job.Path, PathHealth.UserPathEntries(), PathHealth.LinksDir, PathHealth.PackagesDir);
            List<string> fixes = plan.Count > 0 ? PathHealth.Apply(plan) : [];
            RunLog.Append(job.LogFile, "=== PATH check\n" + (fixes.Count == 0 ? "All commands still reachable" : string.Join("\n", fixes)));
            if (fixes.Count > 0)
            {
                Emit(new ProgressEvent { Key = "path-health", Status = ItemStatus.Done, Message = string.Join(". ", fixes) + "." });
            }
        }

        Emit(new ProgressEvent { Finished = true });
    }

    private static ProgressEvent Done(JobItem ji, string message, ProcResult r, bool reboot = false) =>
        new() { Key = ji.Key, Status = ItemStatus.Done, Message = message, Reboot = reboot, Output = r.Tail(12).ToList() };

    private static ProgressEvent Fail(JobItem ji, string message, ProcResult r) =>
        new() { Key = ji.Key, Status = ItemStatus.Failed, Message = message, Output = r.Tail(12).ToList() };
}

public static class WorkerLauncher
{
    /// <summary>Starts this exe elevated in worker mode. Null when the UAC prompt was declined.</summary>
    public static Process? Start(string jobPath)
    {
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("Unknown executable path");
        try
        {
            return Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = $"--worker \"{jobPath}\"",
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = AppContext.BaseDirectory,
            });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return null;
        }
    }
}

/// <summary>Follows the helper's progress file from the window side.</summary>
public sealed class ProgressReader(string path)
{
    private long _offset;
    private string _partial = "";

    public List<ProgressEvent> ReadNew()
    {
        var events = new List<ProgressEvent>();
        if (!File.Exists(path))
        {
            return events;
        }
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (fs.Length <= _offset)
        {
            return events;
        }
        fs.Seek(_offset, SeekOrigin.Begin);
        using var sr = new StreamReader(fs);
        string text = _partial + sr.ReadToEnd();
        _offset = fs.Length;
        int last = text.LastIndexOf('\n');
        _partial = last < 0 ? text : text[(last + 1)..];
        if (last < 0)
        {
            return events;
        }
        foreach (string line in text[..last].Split('\n'))
        {
            string t = line.Trim();
            if (t.Length == 0)
            {
                continue;
            }
            try
            {
                if (JsonSerializer.Deserialize<ProgressEvent>(t, Json.Options) is { } e)
                {
                    events.Add(e);
                }
            }
            catch (JsonException)
            {
            }
        }
        return events;
    }
}
