using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;

namespace SafeUpdater.Core;

/// <summary>
/// One "Update selected" press. Admin items go to the elevated helper in one batch (one UAC prompt); the
/// user-level tools (Scoop, npm, pip, rustup, claude) run here as the user, so their files stay the user's.
/// </summary>
public sealed class UpdateRun(Settings settings, string smokeScript)
{
    public event Action<UpdateItem>? Changed;
    public event Action<string>? Status;

    private RunSummary _summary = new();

    public async Task<RunSummary> RunAsync(IReadOnlyList<UpdateItem> requested, CancellationToken ct = default)
    {
        // Hidden items are unsafe by policy; never act on one, whatever the caller passed. One item per key.
        List<UpdateItem> items = requested
            .Where(i => i.Selectable && !i.Hidden)
            .GroupBy(i => i.Key)
            .Select(g => g.First())
            .ToList();
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        _summary = new RunSummary { LogFile = RunLog.NewRunLog(stamp) };
        Log($"Safe Updater run {DateTime.Now:yyyy-MM-dd HH:mm}, {items.Count} items");
        foreach (UpdateItem i in items)
        {
            i.Result = null;
            i.Output.Clear();
            i.RetryLocally = false;
            i.RebootNeeded = false;
            Set(i, ItemStatus.Queued, "Waiting");
        }

        List<UpdateItem> admin = items.Where(i => i.NeedsAdmin).ToList();
        if (admin.Count > 0)
        {
            await RunAdminAsync(admin, stamp, ct);
            foreach (UpdateItem i in admin.Where(i => i.RetryLocally && i.Source == UpdateSource.Apps))
            {
                await RetryLocallyAsync(i, ct);
            }
        }

        foreach (UpdateItem i in items.Where(i => i.Source == UpdateSource.Scoop))
        {
            await RunScoopAsync(i, ct);
        }
        await RunNodeAsync(items.Where(i => i.Source == UpdateSource.Node).ToList(), ct);
        await RunPythonAsync(items.Where(i => i.Source == UpdateSource.Python).ToList(), stamp, ct);
        await RunRustAsync(items.Where(i => i.Source == UpdateSource.Rust).ToList(), ct);
        foreach (UpdateItem i in items.Where(i => i.Source == UpdateSource.ClaudeCode))
        {
            await RunSimpleAsync(i, Tools.Claude(), ["update"], TimeSpan.FromMinutes(10), ct);
        }

        DateTime now = DateTime.Now;
        foreach (UpdateItem i in items)
        {
            if (i.Status is ItemStatus.Queued or ItemStatus.Running)
            {
                Set(i, ItemStatus.Failed, i.Result ?? "Didn't run");
            }
            RunLog.Record(i, now);
            switch (i.Status)
            {
                case ItemStatus.Done: _summary.Updated++; break;
                case ItemStatus.Failed: _summary.Failed++; break;
                default: _summary.Skipped++; break;
            }
            _summary.RebootRecommended |= i.RebootNeeded;
        }
        settings.LastUpdated = now;
        settings.Save();
        return _summary;
    }

    private async Task RunAdminAsync(List<UpdateItem> admin, string stamp, CancellationToken ct)
    {
        var job = new WorkerJob
        {
            Stamp = stamp,
            Items = admin.Select(JobItem.From).ToList(),
            ProgressFile = Path.Combine(AppPaths.Jobs, $"progress-{stamp}.jsonl"),
            LogFile = _summary.LogFile,
            RequestingUserSid = WindowsIdentity.GetCurrent().User?.Value,
            RestorePoint = settings.RestorePoint,
        };
        try
        {
            job.Path = PathHealth.Take();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RunLog.WriteError("PATH snapshot failed: " + ex.Message);
        }
        string jobPath = Path.Combine(AppPaths.Jobs, $"job-{stamp}.json");
        await File.WriteAllTextAsync(jobPath, JsonSerializer.Serialize(job, Json.Options), ct);

        Status?.Invoke("Waiting for you to allow admin access…");
        Process? p = WorkerLauncher.Start(jobPath);
        if (p is null)
        {
            foreach (UpdateItem i in admin)
            {
                Set(i, ItemStatus.Skipped, "Skipped: admin access was declined");
            }
            _summary.Notes.Add("App, Chocolatey and Windows updates were skipped because admin access was declined.");
            return;
        }

        Status?.Invoke("Updating apps and Windows…");
        var reader = new ProgressReader(job.ProgressFile);
        Dictionary<string, UpdateItem> byKey = admin.ToDictionary(i => i.Key);

        bool Drain()
        {
            foreach (ProgressEvent e in reader.ReadNew())
            {
                if (e.Finished)
                {
                    return true;
                }
                switch (e.Key)
                {
                    case "status" when e.Message is not null:
                        Status?.Invoke(e.Message);
                        continue;
                    case "path-health" or "restore-point" when e.Message is not null:
                        _summary.Notes.Add(e.Message);
                        continue;
                }
                if (e.Key is null || !byKey.TryGetValue(e.Key, out UpdateItem? item))
                {
                    continue;
                }
                item.RetryLocally = e.RetryLocally;
                item.RebootNeeded |= e.Reboot;
                if (e.Output is not null)
                {
                    item.Output.Clear();
                    item.Output.AddRange(e.Output);
                }
                Set(item, e.Status, e.Message);
                if (e.Status == ItemStatus.Running)
                {
                    Status?.Invoke($"Installing {item.Name}…");
                }
            }
            return false;
        }

        bool finished = false;
        while (!finished)
        {
            await Task.Delay(500, ct);
            bool exited = p.HasExited;
            finished = Drain();
            if (exited && !finished)
            {
                Drain();
                finished = true;
            }
        }
        p.Dispose();
        foreach (UpdateItem i in admin.Where(i => i.Status is ItemStatus.Queued or ItemStatus.Running && !i.RetryLocally))
        {
            Set(i, ItemStatus.Failed, "The admin helper stopped before finishing this one. The log has details.");
        }
    }

    private async Task RetryLocallyAsync(UpdateItem item, CancellationToken ct)
    {
        Set(item, ItemStatus.Running, "Installing as you…");
        Log($"=== {item.Name} [{item.Id}] (as the user)");
        ProcResult r = await ProcessRunner.RunAsync(Tools.Winget(), WingetParser.UpgradeArgs(item, force: false), TimeSpan.FromMinutes(45), ct: ct);
        Log(r.Output + $"exit {r.ExitCode}");
        Keep(item, r);
        WingetOutcome o = WingetParser.Classify(r.ExitCode, r.Output, elevated: false);
        item.RebootNeeded |= o == WingetOutcome.OkReboot;
        Set(item, o is WingetOutcome.Ok or WingetOutcome.OkReboot or WingetOutcome.AlreadyCurrent ? ItemStatus.Done : ItemStatus.Failed,
            o switch
            {
                WingetOutcome.Ok or WingetOutcome.OkReboot => "Updated (as you, without admin)",
                WingetOutcome.AlreadyCurrent => "Already up to date",
                _ => "Its installer failed with and without admin. It may update itself when you open it.",
            });
    }

    private async Task RunScoopAsync(UpdateItem item, CancellationToken ct)
    {
        Set(item, ItemStatus.Running, "Installing…");
        Status?.Invoke($"Updating {item.Name}…");
        Log($"=== {item.Name} (Scoop)");
        ProcResult r = await ProcessRunner.PowerShellAsync(ScoopSource.UpdateScript(item), TimeSpan.FromMinutes(20), ct);
        Log(r.Output + $"exit {r.ExitCode}");
        Keep(item, r);
        Set(item, r.Ok ? ItemStatus.Done : ItemStatus.Failed, r.Ok ? "Updated" : "Didn't install: " + (r.Tail(1).FirstOrDefault() ?? $"exit {r.ExitCode}"));
    }

    private async Task RunNodeAsync(List<UpdateItem> items, CancellationToken ct)
    {
        string? npm = Tools.Npm();
        foreach (UpdateItem i in items)
        {
            await RunSimpleAsync(i, npm, ["install", "-g", $"{i.Id}@{i.Available}"], TimeSpan.FromMinutes(15), ct);
            if (i.Status == ItemStatus.Done && i.Output.Any(l => l.Contains("install-scripts", StringComparison.OrdinalIgnoreCase)))
            {
                i.Result = "Updated. npm skipped a dependency's install script (its allowScripts setting); if the tool misbehaves, reinstall it with --allow-scripts.";
                Changed?.Invoke(i);
            }
        }
    }

    private async Task RunRustAsync(List<UpdateItem> items, CancellationToken ct)
    {
        string? rustup = Tools.Rustup();
        foreach (UpdateItem i in items.OrderBy(i => i.Id == "rustup" ? 0 : 1))
        {
            await RunSimpleAsync(i, rustup, i.Id == "rustup" ? ["self", "update"] : ["update", i.Id], TimeSpan.FromMinutes(20), ct);
        }
    }

    private static string[] PipInstall(string? scope, bool upgrade)
    {
        var a = new List<string> { "-m", "pip", "install", "--disable-pip-version-check", "--no-warn-script-location" };
        if (scope == "user")
        {
            a.Add("--user");
        }
        if (upgrade)
        {
            a.Add("--upgrade");
        }
        return [.. a];
    }

    private async Task RunPythonAsync(List<UpdateItem> items, string stamp, CancellationToken ct)
    {
        if (items.Count == 0)
        {
            return;
        }
        string? py = Tools.Python();
        if (py is null)
        {
            foreach (UpdateItem i in items)
            {
                Set(i, ItemStatus.Failed, "Python was not found");
            }
            return;
        }

        // Snapshot both locations so any package can go back exactly where it was.
        Status?.Invoke("Saving a snapshot of your Python packages…");
        string snapScript = Path.Combine(AppPaths.Root, "snapshot.py");
        await File.WriteAllTextAsync(snapScript, PythonSource.SnapshotScript, ct);
        var snap = PythonSource.ParseSnapshot((await Py(py, [snapScript], 3, ct)).Output);
        var before = new Dictionary<string, (string Name, string Version)>(StringComparer.OrdinalIgnoreCase);
        if (snap is { } s)
        {
            string userFile = Path.Combine(AppPaths.Snapshots, $"pip-{stamp}-user.txt");
            await File.WriteAllLinesAsync(userFile, s.User, ct);
            await File.WriteAllLinesAsync(Path.Combine(AppPaths.Snapshots, $"pip-{stamp}-env.txt"), s.Env, ct);
            settings.LastPipSnapshot = userFile;
            settings.Save();
            foreach (string line in s.Env.Concat(s.User))
            {
                foreach (var kv in PythonSource.ParseFreeze(line))
                {
                    before[kv.Key] = kv.Value;
                }
            }
        }

        SmokeTest.Result? baseline = null;
        if (settings.TestAfterUpdate)
        {
            Status?.Invoke("Testing your tools before updating…");
            baseline = await SmokeTest.RunAsync(py, smokeScript, ct);
            Log("=== baseline test\n" + baseline.Raw);
        }

        foreach (IGrouping<string?, UpdateItem> group in items.GroupBy(i => i.Scope))
        {
            List<UpdateItem> batch = group.ToList();
            string[] specs = batch.Select(i => $"{i.Id}=={i.Available}").ToArray();

            // Look at the whole plan first: a dependency pulled along must not jump a major version or go backwards.
            Status?.Invoke("Checking what the Python update would change…");
            ProcResult plan = await Py(py, [.. PipInstall(group.Key, upgrade: true), "--dry-run", "--quiet", "--report", "-", .. specs], 10, ct);
            List<(string Name, string Version)>? planned = PythonSource.ParsePlan(plan.Output);
            if (planned is not null)
            {
                var picked = new HashSet<string>(batch.Select(i => PythonSource.Normalize(i.Id)), StringComparer.OrdinalIgnoreCase);
                List<string> risky = PythonSource.RiskyPlanChanges(planned, picked, before);
                if (risky.Count > 0)
                {
                    foreach (UpdateItem i in batch)
                    {
                        Set(i, ItemStatus.Skipped, "Not installed: together these would also change " + string.Join(", ", risky) + ", which nobody picked (a major version or a downgrade).");
                    }
                    _summary.Notes.Add("Some Python updates were skipped because pip would also have changed " + string.Join(", ", risky) + ".");
                    continue;
                }
            }

            Status?.Invoke("Updating Python packages…");
            foreach (UpdateItem i in batch)
            {
                Set(i, ItemStatus.Running, "Installing…");
            }
            ProcResult r = await Py(py, [.. PipInstall(group.Key, upgrade: true), .. specs], 40, ct);
            if (r.Ok)
            {
                foreach (UpdateItem i in batch)
                {
                    Set(i, ItemStatus.Done, "Updated");
                }
            }
            else
            {
                // One bad package should not hold back the rest.
                foreach (UpdateItem i in batch)
                {
                    ProcResult one = await Py(py, [.. PipInstall(group.Key, upgrade: true), $"{i.Id}=={i.Available}"], 20, ct);
                    Keep(i, one);
                    Set(i, one.Ok ? ItemStatus.Done : ItemStatus.Failed, one.Ok ? "Updated" : "Didn't install: " + one.Tail(1).FirstOrDefault());
                }
            }
        }

        Dictionary<string, UpdateItem> changed = items
            .Where(i => i.Status == ItemStatus.Done)
            .ToDictionary(i => PythonSource.Normalize(i.Id), StringComparer.OrdinalIgnoreCase);
        if (changed.Count == 0)
        {
            return;
        }

        // If an update broke another package's version rule, put that package back.
        ProcResult check = await Py(py, ["-m", "pip", "check", "--disable-pip-version-check"], 3, ct);
        foreach (PythonSource.Conflict c in PythonSource.ParsePipCheck(check.Output))
        {
            string key = PythonSource.Normalize(c.Have);
            if (changed.TryGetValue(key, out UpdateItem? item) && before.TryGetValue(key, out var old))
            {
                await Py(py, [.. PipInstall(item.Scope, upgrade: false), $"{old.Name}=={old.Version}"], 10, ct);
                Set(item, ItemStatus.Skipped, $"Kept at {old.Version}: {c.Package} needs {c.Requirement}");
                changed.Remove(key);
            }
        }

        // Playwright needs a matching browser build, but only fetch one if this PC already used it.
        if (changed.ContainsKey("playwright") && PlaywrightChromiumInstalled())
        {
            Status?.Invoke("Updating Playwright's browser…");
            await Py(py, ["-m", "playwright", "install", "chromium"], 15, ct);
        }

        if (!settings.TestAfterUpdate || changed.Count == 0)
        {
            return;
        }
        Status?.Invoke("Testing your tools after updating…");
        SmokeTest.Result after = await SmokeTest.RunAsync(py, smokeScript, ct);
        Log("=== test after updating\n" + after.Raw);
        List<string> broke = SmokeTest.NewFailures(baseline, after);
        if (broke.Count == 0)
        {
            _summary.Notes.Add(after.Ran && after.Passed.Count + after.Failed.Count > 0
                ? $"Your tools passed {after.Passed.Count} of {after.Passed.Count + after.Failed.Count} checks after the Python update."
                : "No tool checks applied to this PC.");
            return;
        }

        // Roll back the major versions first (the usual culprit); if that isn't enough, everything.
        string brokeText = string.Join(", ", broke);
        List<UpdateItem> majors = changed.Values.Where(i => i.IsMajor).ToList();
        if (majors.Count > 0 && majors.Count < changed.Count)
        {
            Status?.Invoke("A check failed. Rolling back the major-version packages…");
            await RollBackAsync(py, majors, before, ct);
            SmokeTest.Result retest = await SmokeTest.RunAsync(py, smokeScript, ct);
            if (SmokeTest.NewFailures(baseline, retest).Count == 0)
            {
                foreach (UpdateItem i in majors)
                {
                    Set(i, ItemStatus.Failed, $"Rolled back: the {brokeText} check failed after this update");
                }
                _summary.Notes.Add($"The {brokeText} check failed after updating, so {string.Join(", ", majors.Select(m => m.Name))} went back to the old version. Everything passes again.");
                return;
            }
        }
        Status?.Invoke("A check failed. Rolling back the Python update…");
        await RollBackAsync(py, changed.Values.ToList(), before, ct);
        foreach (UpdateItem i in changed.Values)
        {
            Set(i, ItemStatus.Failed, $"Rolled back: the {brokeText} check failed after updating");
        }
        SmokeTest.Result final = await SmokeTest.RunAsync(py, smokeScript, ct);
        _summary.Notes.Add(SmokeTest.NewFailures(baseline, final).Count == 0
            ? $"The {brokeText} check failed after the Python update, so it was rolled back. Everything passes again."
            : $"The {brokeText} check still fails after rolling back. Use \"Roll back the last Python update\" or check the log.");
    }

    private async Task RollBackAsync(string py, List<UpdateItem> items, Dictionary<string, (string Name, string Version)> before, CancellationToken ct)
    {
        foreach (IGrouping<string?, UpdateItem> group in items.GroupBy(i => i.Scope))
        {
            string[] pins = group
                .Select(i => PythonSource.Normalize(i.Id))
                .Where(before.ContainsKey)
                .Select(k => $"{before[k].Name}=={before[k].Version}")
                .ToArray();
            if (pins.Length > 0)
            {
                await Py(py, [.. PipInstall(group.Key, upgrade: false), .. pins], 30, ct);
            }
        }
    }

    private static bool PlaywrightChromiumInstalled()
    {
        string root = Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ms-playwright");
        return Directory.Exists(root) && Directory.GetDirectories(root, "chromium*").Length > 0;
    }

    /// <summary>Puts packages back to a snapshot: the user-site file with --user, its "-env" twin without.</summary>
    public static async Task<ProcResult> RestoreSnapshotAsync(string userSnapshot, CancellationToken ct = default)
    {
        string py = Tools.Python() ?? throw new InvalidOperationException("Python was not found");
        string envSnapshot = userSnapshot.Replace("-user.txt", "-env.txt", StringComparison.OrdinalIgnoreCase);
        ProcResult last = new(0, "Nothing to restore", false);
        foreach ((string file, string? scope) in new[] { (userSnapshot, "user"), (envSnapshot, (string?)null) })
        {
            if (!File.Exists(file) || new FileInfo(file).Length == 0 || (scope is null && file == userSnapshot))
            {
                continue;
            }
            last = await ProcessRunner.RunAsync(py, [.. PipInstall(scope, upgrade: false), "-r", file], TimeSpan.FromMinutes(40), ct: ct);
            if (!last.Ok)
            {
                return last;
            }
        }
        return last;
    }

    private async Task RunSimpleAsync(UpdateItem item, string? exe, string[] args, TimeSpan timeout, CancellationToken ct)
    {
        if (exe is null)
        {
            Set(item, ItemStatus.Failed, "The tool was not found");
            return;
        }
        Set(item, ItemStatus.Running, "Installing…");
        Status?.Invoke($"Updating {item.Name}…");
        Log($"=== {item.Name}: {Path.GetFileName(exe)} {string.Join(' ', args)}");
        ProcResult r = await ProcessRunner.RunAsync(exe, args, timeout, ct: ct);
        Log(r.Output + $"exit {r.ExitCode}");
        Keep(item, r);
        Set(item, r.Ok ? ItemStatus.Done : ItemStatus.Failed, r.Ok ? "Updated" : "Didn't install: " + (r.Tail(1).FirstOrDefault() ?? $"exit {r.ExitCode}"));
    }

    private async Task<ProcResult> Py(string py, string[] args, int minutes, CancellationToken ct)
    {
        Log($"=== python {string.Join(' ', args)}");
        ProcResult r = await ProcessRunner.RunAsync(py, args, TimeSpan.FromMinutes(minutes), ct: ct);
        Log(r.Output + $"exit {r.ExitCode}");
        return r;
    }

    private static void Keep(UpdateItem item, ProcResult r)
    {
        item.Output.Clear();
        item.Output.AddRange(r.Tail(12));
    }

    private void Set(UpdateItem item, ItemStatus status, string? message)
    {
        item.Status = status;
        if (message is not null)
        {
            item.Result = message;
        }
        Changed?.Invoke(item);
    }

    private void Log(string text) => RunLog.Append(_summary.LogFile, text);
}
