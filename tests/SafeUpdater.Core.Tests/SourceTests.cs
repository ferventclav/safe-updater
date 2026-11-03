using SafeUpdater.Core;

namespace SafeUpdater.Core.Tests;

/// <summary>The parsers and checks that decide, per PC, what a source may touch.</summary>
public class SourceTests
{
    [Fact]
    public void Chocolatey_outdated()
    {
        string output = "Chocolatey v2.4.1\ngit|2.55.0|2.56.0|false\nnodejs|24.15.0|26.13.0|false\n7zip.install|26.04|26.05|true\nnot a row\nodd|1.0|n/a|false\n";

        List<UpdateItem> items = ChocolateySource.ParseOutdated(output);

        Assert.Equal(["git", "nodejs", "7zip.install"], items.Select(i => i.Id));
        Assert.True(items[1].IsMajor);
        Assert.True(items[2].Pinned);
        Assert.All(items, i => Assert.True(i.NeedsAdmin));
        Assert.All(items, i => Assert.Equal(UpdateSource.Chocolatey, i.Source));
    }

    [Theory]
    [InlineData("Git.Git", "Git", "git", "git")]
    [InlineData("7zip.7zip", "7-Zip 26.04 (x64 edition)", "7zip.install", "7zip.install")]
    [InlineData("Google.Chrome", "Google Chrome", "googlechrome", "googlechrome")]
    [InlineData("Mozilla.Firefox", "Mozilla Firefox (x64 en-US)", "firefox", "firefox")]
    [InlineData("Microsoft.VisualStudioCode", "Microsoft Visual Studio Code (User)", "vscode", "vscode")]
    [InlineData("GitHub.cli", "GitHub CLI", "gh", "gh")]
    [InlineData("OpenJS.NodeJS.LTS", "Node.js", "nodejs-lts", "nodejs-lts")]
    [InlineData("GitHub.cli", "GitHub CLI", "git", null)]
    [InlineData("Python.Launcher", "Python Launcher", "python", null)]
    [InlineData("Chocolatey.Chocolatey", "Chocolatey", "chocolatey", null)]
    [InlineData("Git.Git", "Git", "go", null)]
    public void Chocolatey_owner_of_a_winget_app(string id, string name, string choco, string? owner)
    {
        var item = new UpdateItem { Source = UpdateSource.Apps, Id = id, Name = name };

        Assert.Equal(owner, ChocolateySource.OwnerOf(item, [choco]));
    }

    [Fact]
    public void Scoop_status_marks_held_global_and_failed_apps_unsafe()
    {
        string json = """
            [{"name":"git","current":"2.55.0","latest":"2.56.0","info":""},
             {"name":"python","current":"3.13.1","latest":"3.14.0","info":"Held package"},
             {"name":"7zip","current":"26.04","latest":"26.04","info":""},
             {"name":"nodejs","current":"24.1.0","latest":"26.0.0","info":"Global install"},
             {"name":"bad;name","current":"1","latest":"2","info":""},
             {"name":"vlc","current":"3.0","latest":"3.1","info":"Install failed"}]
            """.Replace("\n", "").Replace("\r", "");

        List<UpdateItem>? items = ScoopSource.ParseStatus("WARN  Scoop was updated\n" + json);

        Assert.NotNull(items);
        Assert.Equal(["git", "python", "nodejs", "vlc"], items.Select(i => i.Id));
        Assert.Null(items[0].UnsafeReason);
        Assert.Contains("unhold", items[1].UnsafeReason);
        Assert.Contains("admin", items[2].UnsafeReason);
        Assert.Contains("failed", items[3].UnsafeReason);
        Assert.Null(ScoopSource.ParseStatus("scoop : The term 'scoop' is not recognized"));
    }

    [Fact]
    public void Scoop_update_script_refuses_odd_names()
    {
        Assert.Equal("scoop update '7zip'", ScoopSource.UpdateScript(new UpdateItem { Source = UpdateSource.Scoop, Id = "7zip", Name = "7zip" }));
        Assert.Throws<ArgumentException>(() => ScoopSource.UpdateScript(new UpdateItem { Source = UpdateSource.Scoop, Id = "x'; rm -r C:\\; '", Name = "x" }));
    }

    [Fact]
    public void Pip_report_with_scope_and_blockers()
    {
        string json = """{"managedBy":null,"items":[{"name":"phonemizer-fork","version":"3.3.1","latest":"3.3.2","scope":"user","writable":true,"installer":"pip","location":"C:\\Users\\me\\AppData\\Roaming\\Python","blockers":["kokoro-onnx needs phonemizer-fork==3.3.1"]},{"name":"pip","version":"25.2","latest":"25.3","scope":"env","writable":false,"installer":"pip","location":"C:\\Program Files\\Python314\\Lib\\site-packages","blockers":[]}]}""";

        PythonSource.Report? r = PythonSource.ParseOutdated("WARNING: something\n" + json);

        Assert.NotNull(r);
        Assert.Null(r.ManagedBy);
        Assert.Equal("kokoro-onnx needs phonemizer-fork==3.3.1", Assert.Single(r.Items[0].Blockers));
        Assert.Equal("env", r.Items[1].Scope);
        Assert.False(r.Items[1].Writable);
        Assert.Null(PythonSource.UnsafeReason(r.ManagedBy, r.Items[0]));
        Assert.Contains("needs admin", PythonSource.UnsafeReason(r.ManagedBy, r.Items[1]));
    }

    [Fact]
    public void Pip_old_array_format_and_errors()
    {
        PythonSource.Report? r = PythonSource.ParseOutdated("""[{"name":"av","version":"18.0.0","latest":"19.0.2","blockers":[]}]""");

        Assert.NotNull(r);
        PythonSource.Outdated av = Assert.Single(r.Items);
        Assert.Equal("user", av.Scope);
        Assert.True(av.Writable);
        Assert.Null(PythonSource.ParseOutdated("""{"error":"pip failed"}"""));
        Assert.Null(PythonSource.ParseOutdated("Traceback (most recent call last):"));
    }

    [Theory]
    [InlineData("conda", "", true, "conda")]
    [InlineData("the operating system", "", true, "system's package manager")]
    [InlineData(null, "conda", true, "conda environment")]
    [InlineData(null, "pip", false, "needs admin")]
    [InlineData(null, "pip", true, null)]
    [InlineData(null, "uv", true, null)]
    public void Pip_unsafe_reasons(string? managedBy, string installer, bool writable, string? expected)
    {
        var o = new PythonSource.Outdated("numpy", "2.5.1", "2.5.4", [], "env", writable, installer, "C:\\Python314\\Lib\\site-packages");

        string? reason = PythonSource.UnsafeReason(managedBy, o);

        if (expected is null)
        {
            Assert.Null(reason);
        }
        else
        {
            Assert.Contains(expected, reason);
        }
    }

    [Fact]
    public void Pip_snapshot_has_both_scopes()
    {
        var snap = PythonSource.ParseSnapshot("""{"user":["numpy==2.5.1","av==18.0.0"],"env":["pip==25.3"]}""");

        Assert.NotNull(snap);
        Assert.Equal(2, snap.Value.User.Count);
        Assert.Equal("pip==25.3", Assert.Single(snap.Value.Env));
        Assert.Null(PythonSource.ParseSnapshot("no json here"));
    }

    [Fact]
    public void Pip_plan_check_flags_unpicked_majors_and_downgrades_only()
    {
        string report = "Would install numpy-2.5.4 av-19.0.2 newdep-1.0\n"
                        + """{"version":"1","install":[{"metadata":{"name":"numpy","version":"2.5.4"}},{"metadata":{"name":"av","version":"19.0.2"}},{"metadata":{"name":"newdep","version":"1.0"}},{"metadata":{"name":"tokenizers","version":"0.22.0"}},{"metadata":{"name":"huggingface_hub","version":"1.34.0"}}]}""";
        var installed = PythonSource.ParseFreeze("numpy==2.5.1\nav==18.0.0\ntokenizers==0.22.1\nhuggingface_hub==1.33.0\n");

        List<(string Name, string Version)>? plan = PythonSource.ParsePlan(report);
        List<string> risky = PythonSource.RiskyPlanChanges(plan!, new HashSet<string> { "numpy" }, installed);

        Assert.Equal(5, plan!.Count);
        Assert.Equal(["av 18.0.0 → 19.0.2", "tokenizers 0.22.1 → 0.22.0"], risky);
        Assert.Empty(PythonSource.RiskyPlanChanges(plan, new HashSet<string> { "numpy", "av", "tokenizers" }, installed));
        Assert.Null(PythonSource.ParsePlan("ERROR: No matching distribution found"));
    }

    [Fact]
    public void Windows_search_reads_installed_driver_date_and_optional_flag()
    {
        string json = """[{"id":"1d1f9b0e-0000-4000-8000-000000000009","title":"Intel - Display - 31.0.101.5000","driver":true,"driverDate":"2025-03-01","installedDate":"2026-02-10","optional":true,"sizeMb":300,"kb":""}]""";

        WindowsUpdateSource.Record r = Assert.Single(WindowsUpdateSource.ParseSearch(json)!);

        Assert.True(r.Driver);
        Assert.Equal(new DateTime(2025, 3, 1), r.DriverDate);
        Assert.Equal(new DateTime(2026, 2, 10), r.InstalledDate);
        Assert.True(r.Optional);
    }

    [Theory]
    [InlineData("CREATED", "Made a restore point")]
    [InlineData("SKIPPED: A new system restore point cannot be created because one has already been created within the past 1440 minutes.", "24 hours")]
    [InlineData("FAILED: The service cannot be started, either because it is disabled", "No restore point")]
    [InlineData("", "No restore point")]
    public void Restore_point_outcomes(string output, string expected)
    {
        Assert.Contains(expected, Preflight.DescribeRestorePoint(output));
    }
}
