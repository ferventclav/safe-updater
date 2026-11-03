using System.Text.Json;
using System.Text.RegularExpressions;

namespace SafeUpdater.Core;

/// <summary>pip packages in the user site (where this machine keeps them).</summary>
public static partial class PythonSource
{
    public sealed record Outdated(string Name, string Version, string Latest, List<string> Blockers,
        string Scope = "user", bool Writable = true, string Installer = "", string Location = "");

    /// <summary>What the check script found: the packages, and whether another tool manages this Python.</summary>
    public sealed record Report(string? ManagedBy, List<Outdated> Items);

    public sealed record Conflict(string Package, string Requirement, string Have, string HaveVersion);

    /// <summary>
    /// Lists outdated packages of this interpreter with where each lives (user site or the interpreter's own
    /// site-packages), whether that folder is writable, and the installed packages whose rules exclude the
    /// newest version (kokoro-onnx pinned phonemizer-fork==3.3.1, so 3.3.2 would break it).
    /// It also reports when conda or the OS manages this Python (PEP 668), where pip must not change anything.
    /// </summary>
    internal const string OutdatedScript = """
        import json, os, site, subprocess, sys, sysconfig, tempfile

        def norm(n):
            return n.lower().replace("_", "-").replace(".", "-")

        def writable(path):
            try:
                with tempfile.TemporaryFile(dir=path):
                    return True
            except Exception:
                return False

        managed = None
        if os.path.isdir(os.path.join(sys.prefix, "conda-meta")):
            managed = "conda"
        else:
            try:
                if os.path.isfile(os.path.join(sysconfig.get_path("stdlib"), "EXTERNALLY-MANAGED")):
                    managed = "the operating system"
            except Exception:
                pass

        out = subprocess.run([sys.executable, "-m", "pip", "list", "--outdated", "--format=json", "-v",
                              "--disable-pip-version-check"], capture_output=True, text=True, encoding="utf-8")
        try:
            items = json.loads(out.stdout or "[]")
        except ValueError:
            print(json.dumps({"error": (out.stderr or out.stdout)[-400:]}))
            sys.exit(0)

        user_site = ""
        try:
            if site.ENABLE_USER_SITE:
                user_site = os.path.normcase(os.path.abspath(site.getusersitepackages()))
        except Exception:
            pass

        Version = None
        reqs = []
        try:
            from importlib import metadata
            from packaging.requirements import Requirement
            from packaging.version import Version
            for d in metadata.distributions():
                who = d.metadata["Name"]
                for r in d.requires or []:
                    try:
                        q = Requirement(r)
                    except Exception:
                        continue
                    if q.marker is not None:
                        try:
                            if not q.marker.evaluate({"extra": ""}):
                                continue
                        except Exception:
                            continue
                    if str(q.specifier):
                        reqs.append((who, q))
        except Exception:
            Version = None

        res = []
        can_write = {}
        for it in items:
            if it.get("editable_project_location"):
                continue  # a source checkout; pip install would replace it with the PyPI release
            loc = it.get("location") or ""
            key = os.path.normcase(os.path.abspath(loc)) if loc else ""
            if key not in can_write:
                can_write[key] = writable(loc) if loc else False
            blockers = set()
            if Version is not None:
                try:
                    latest = Version(it["latest_version"])
                    for who, q in reqs:
                        if norm(q.name) == norm(it["name"]) and not q.specifier.contains(latest, prereleases=True):
                            blockers.add(f"{who} needs {q.name}{q.specifier}")
                except Exception:
                    pass
            res.append({"name": it["name"], "version": it["version"], "latest": it["latest_version"],
                        "scope": "user" if user_site and key.startswith(user_site) else "env",
                        "writable": can_write[key], "installer": it.get("installer") or "", "location": loc,
                        "blockers": sorted(blockers)})
        print(json.dumps({"managedBy": managed, "items": res}))
        """;

    public static async Task<SourceResult> CheckAsync(CancellationToken ct)
    {
        string? py = Tools.Python();
        if (py is null)
        {
            return new SourceResult { Source = UpdateSource.Python, Absent = true };
        }
        string script = Path.Combine(AppPaths.Root, "outdated.py");
        await File.WriteAllTextAsync(script, OutdatedScript, ct);
        ProcResult r = await ProcessRunner.RunAsync(py, [script], TimeSpan.FromMinutes(4), ct: ct);
        Report? report = ParseOutdated(r.Output);
        if (report is null)
        {
            return new SourceResult { Source = UpdateSource.Python, Failed = true, Message = "pip could not check: " + r.Tail(1).FirstOrDefault() };
        }
        var items = report.Items.Select(o => new UpdateItem
        {
            Source = UpdateSource.Python,
            Id = o.Name,
            Name = o.Name,
            Current = o.Version,
            Available = o.Latest,
            IsMajor = Versions.IsMajorJump(o.Version, o.Latest),
            Blockers = o.Blockers,
            Scope = o.Scope,
            UnsafeReason = UnsafeReason(report.ManagedBy, o),
        }).ToList();
        return new SourceResult
        {
            Source = UpdateSource.Python,
            Items = items,
            Message = items.Count == 0 ? "All Python packages are up to date" : null,
        };
    }

    /// <summary>Why pip must not touch this package on this PC, or null when it may.</summary>
    public static string? UnsafeReason(string? managedBy, Outdated o)
    {
        if (managedBy is not null)
        {
            return $"This Python is managed by {managedBy}. Changing its packages with pip can break it, so update them with {(managedBy == "conda" ? "conda" : "the system's package manager")} instead.";
        }
        if (o.Installer.Equals("conda", StringComparison.OrdinalIgnoreCase))
        {
            return "Installed by conda. Changing it with pip can break the conda environment.";
        }
        if (!o.Writable)
        {
            return $"Installed for all users in {o.Location}, which needs admin to change. Update it from an admin terminal if you need to.";
        }
        return null;
    }

    public static Report? ParseOutdated(string output)
    {
        string? json = output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith('[') || l.StartsWith('{'));
        if (json is null)
        {
            return null;
        }
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out _))
            {
                return null;
            }
            string? managed = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("managedBy", out JsonElement m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            JsonElement list = root.ValueKind == JsonValueKind.Array ? root : root.GetProperty("items");
            var items = list.EnumerateArray().Select(e => new Outdated(
                e.GetProperty("name").GetString() ?? "",
                e.GetProperty("version").GetString() ?? "",
                e.GetProperty("latest").GetString() ?? "",
                e.TryGetProperty("blockers", out JsonElement b) ? b.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : [],
                e.TryGetProperty("scope", out JsonElement s) ? s.GetString() ?? "user" : "user",
                !e.TryGetProperty("writable", out JsonElement w) || w.ValueKind != JsonValueKind.False,
                e.TryGetProperty("installer", out JsonElement ins) ? ins.GetString() ?? "" : "",
                e.TryGetProperty("location", out JsonElement loc) ? loc.GetString() ?? "" : "")).ToList();
            return new Report(managed, items);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Every installed package by scope, for the snapshot taken before a pip update. Editable installs are left out.</summary>
    internal const string SnapshotScript = """
        import json, os, site, subprocess, sys
        out = subprocess.run([sys.executable, "-m", "pip", "list", "--format=json", "-v", "--disable-pip-version-check"],
                             capture_output=True, text=True, encoding="utf-8")
        user = ""
        try:
            if site.ENABLE_USER_SITE:
                user = os.path.normcase(os.path.abspath(site.getusersitepackages()))
        except Exception:
            pass
        res = {"user": [], "env": []}
        for p in json.loads(out.stdout or "[]"):
            if p.get("editable_project_location"):
                continue
            loc = os.path.normcase(os.path.abspath(p.get("location") or ""))
            res["user" if user and loc.startswith(user) else "env"].append(p["name"] + "==" + p["version"])
        print(json.dumps(res))
        """;

    public static (List<string> User, List<string> Env)? ParseSnapshot(string output)
    {
        string? json = output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith('{'));
        if (json is null)
        {
            return null;
        }
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            List<string> Read(string k) => doc.RootElement.GetProperty(k).EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList();
            return (Read("user"), Read("env"));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Packages "pip install --dry-run --report -" would install, as (name, version).</summary>
    public static List<(string Name, string Version)>? ParsePlan(string output)
    {
        int start = output.IndexOf('{');
        int end = output.LastIndexOf('}');
        if (start < 0 || end < start)
        {
            return null;
        }
        try
        {
            using JsonDocument doc = JsonDocument.Parse(output[start..(end + 1)]);
            return doc.RootElement.GetProperty("install").EnumerateArray()
                .Select(e => e.GetProperty("metadata"))
                .Select(m => (m.GetProperty("name").GetString() ?? "", m.GetProperty("version").GetString() ?? ""))
                .ToList();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Changes in a pip install plan that nobody picked and that could break things: a major jump or a downgrade of
    /// an installed package pulled in as a dependency. New packages and routine updates of dependencies are fine.
    /// </summary>
    public static List<string> RiskyPlanChanges(IEnumerable<(string Name, string Version)> plan, IReadOnlySet<string> picked,
        IReadOnlyDictionary<string, (string Name, string Version)> installed)
    {
        var risky = new List<string>();
        foreach ((string name, string version) in plan)
        {
            string key = Normalize(name);
            if (picked.Contains(key) || !installed.TryGetValue(key, out var old))
            {
                continue;
            }
            if (Versions.IsMajorJump(old.Version, version) || Versions.Compare(version, old.Version) < 0)
            {
                risky.Add($"{old.Name} {old.Version} → {version}");
            }
        }
        return risky;
    }

    [GeneratedRegex(@"^(?<pkg>\S+) \S+ has requirement (?<req>[^,]+), but you have (?<have>\S+) (?<hv>\S+?)\.?$")]
    private static partial Regex CheckLine();

    /// <summary>Lines like "kokoro-onnx 0.4.7 has requirement phonemizer-fork==3.3.1, but you have phonemizer-fork 3.3.2."</summary>
    public static List<Conflict> ParsePipCheck(string output) =>
        output.Split('\n')
            .Select(l => CheckLine().Match(l.Trim()))
            .Where(m => m.Success)
            .Select(m => new Conflict(m.Groups["pkg"].Value, m.Groups["req"].Value.Trim(), m.Groups["have"].Value, m.Groups["hv"].Value))
            .ToList();

    /// <summary>"name==version" lines from "pip freeze --user", keyed by normalised name.</summary>
    public static Dictionary<string, (string Name, string Version)> ParseFreeze(string text)
    {
        var map = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in text.Split('\n').Select(l => l.Trim()))
        {
            int i = line.IndexOf("==", StringComparison.Ordinal);
            if (i > 0 && !line.StartsWith('#'))
            {
                string name = line[..i];
                map[Normalize(name)] = (name, line[(i + 2)..]);
            }
        }
        return map;
    }

    public static string Normalize(string name) => name.ToLowerInvariant().Replace('_', '-').Replace('.', '-');
}

/// <summary>Global npm packages.</summary>
public static class NodeSource
{
    public static async Task<SourceResult> CheckAsync(CancellationToken ct)
    {
        string? npm = Tools.Npm();
        if (npm is null)
        {
            return new SourceResult { Source = UpdateSource.Node, Absent = true };
        }
        // npm exits with 1 when something is outdated, so the exit code says nothing about success.
        ProcResult r = await ProcessRunner.RunAsync(npm, ["outdated", "-g", "--json"], TimeSpan.FromMinutes(3), ct: ct);
        List<UpdateItem>? items = ParseOutdated(r.Output);
        if (items is null)
        {
            return new SourceResult { Source = UpdateSource.Node, Failed = true, Message = "npm could not check: " + r.Tail(1).FirstOrDefault() };
        }
        // On some PCs the global folder is under Program Files; updating there needs an admin terminal.
        string prefix = (await ProcessRunner.RunAsync(npm, ["prefix", "-g"], TimeSpan.FromMinutes(1), ct: ct)).Lines.LastOrDefault()?.Trim() ?? "";
        if (items.Count > 0 && prefix.Length > 0 && Directory.Exists(prefix) && !Tools.CanWrite(prefix))
        {
            foreach (UpdateItem i in items)
            {
                i.UnsafeReason = $"npm's global folder ({prefix}) needs admin on this PC. Update it from an admin terminal with npm install -g {i.Id}.";
            }
        }
        return new SourceResult
        {
            Source = UpdateSource.Node,
            Items = items,
            Message = items.Count == 0 ? "All global npm packages are up to date" : null,
        };
    }

    public static List<UpdateItem>? ParseOutdated(string output)
    {
        int start = output.IndexOf('{');
        int end = output.LastIndexOf('}');
        if (start < 0 || end < start)
        {
            return output.Trim().Length == 0 ? [] : null;
        }
        try
        {
            using JsonDocument doc = JsonDocument.Parse(output[start..(end + 1)]);
            var items = new List<UpdateItem>();
            foreach (JsonProperty p in doc.RootElement.EnumerateObject())
            {
                string current = p.Value.TryGetProperty("current", out JsonElement c) ? c.GetString() ?? "" : "";
                string latest = p.Value.TryGetProperty("latest", out JsonElement l) ? l.GetString() ?? "" : "";
                if (latest.Length == 0 || current == latest)
                {
                    continue;
                }
                items.Add(new UpdateItem
                {
                    Source = UpdateSource.Node,
                    Id = p.Name,
                    Name = p.Name,
                    Current = current,
                    Available = latest,
                    IsMajor = Versions.IsMajorJump(current, latest),
                });
            }
            return items;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>rustup and its toolchains.</summary>
public static partial class RustSource
{
    [GeneratedRegex(@"^(?<name>\S+) - [Uu]pdate available\s*:\s*(?<cur>\d[\w.\-]*)(?:\s*\([^)]*\))?\s*->\s*(?<new>\d[\w.\-]*)")]
    private static partial Regex UpdateLine();

    public static async Task<SourceResult> CheckAsync(CancellationToken ct)
    {
        string? rustup = Tools.Rustup();
        if (rustup is null)
        {
            return new SourceResult { Source = UpdateSource.Rust, Absent = true };
        }
        ProcResult r = await ProcessRunner.RunAsync(rustup, ["check"], TimeSpan.FromMinutes(2), ct: ct);
        List<UpdateItem> items = ParseCheck(r.Output);
        return new SourceResult
        {
            Source = UpdateSource.Rust,
            Items = items,
            Message = items.Count == 0 ? "Rust is up to date" : null,
        };
    }

    public static List<UpdateItem> ParseCheck(string output) =>
        output.Split('\n')
            .Select(l => UpdateLine().Match(l.Trim()))
            .Where(m => m.Success)
            .Select(m => new UpdateItem
            {
                Source = UpdateSource.Rust,
                Id = m.Groups["name"].Value,
                Name = m.Groups["name"].Value == "rustup" ? "rustup (the installer)" : "Rust " + m.Groups["name"].Value.Split('-')[0] + " toolchain",
                Current = m.Groups["cur"].Value,
                Available = m.Groups["new"].Value,
            })
            .OrderBy(i => i.Id == "rustup" ? 0 : 1)
            .ToList();
}

/// <summary>The Claude Code command-line tool (the desktop app carries its own copy and updates itself).</summary>
public static partial class ClaudeCodeSource
{
    [GeneratedRegex(@"(\d+\.\d+\.\d+)")]
    private static partial Regex Semver();

    public static async Task<SourceResult> CheckAsync(CancellationToken ct)
    {
        string? claude = Tools.Claude();
        if (claude is null)
        {
            return new SourceResult { Source = UpdateSource.ClaudeCode, Absent = true };
        }
        string? current = ParseVersion((await ProcessRunner.RunAsync(claude, ["--version"], TimeSpan.FromMinutes(1), ct: ct)).Output);
        string? npm = Tools.Npm();
        string? latest = npm is null
            ? null
            : ParseVersion((await ProcessRunner.RunAsync(npm, ["view", "@anthropic-ai/claude-code", "version"], TimeSpan.FromMinutes(1), ct: ct)).Output);
        if (current is null || latest is null)
        {
            return new SourceResult { Source = UpdateSource.ClaudeCode, Message = current is null ? "Couldn't read the Claude Code version" : $"Claude Code {current} (couldn't reach npm to compare)" };
        }
        if (Versions.Compare(latest, current) <= 0)
        {
            return new SourceResult { Source = UpdateSource.ClaudeCode, Message = $"Claude Code {current} is the newest" };
        }
        return new SourceResult
        {
            Source = UpdateSource.ClaudeCode,
            Items =
            [
                new UpdateItem { Source = UpdateSource.ClaudeCode, Id = "claude-code", Name = "Claude Code", Current = current, Available = latest },
            ],
        };
    }

    public static string? ParseVersion(string output)
    {
        Match m = Semver().Match(output);
        return m.Success ? m.Groups[1].Value : null;
    }
}
