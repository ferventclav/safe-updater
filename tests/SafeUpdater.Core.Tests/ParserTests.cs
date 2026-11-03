using System.Text.Json;
using SafeUpdater.Core;

namespace SafeUpdater.Core.Tests;

/// <summary>Parsers, fed with the output the tools actually printed on 3 Nov 2026.</summary>
public class ParserTests
{
    private static readonly int[] Widths = [55, 35, 30, 30];

    private static string Row(params string[] cells) =>
        string.Concat(cells.Select((c, i) => i < Widths.Length ? c.PadRight(Widths[i]) : c));

    private static string Table(params string[][] rows) =>
        Row("Name", "Id", "Version", "Available", "Source") + "\n" + new string('-', 156) + "\n"
        + string.Join("\n", rows.Select(r => Row(r))) + "\n";

    [Fact]
    public void Winget_table_with_spinner_noise_and_footer()
    {
        string output = "\r   - \r   \\ \r" + Table(
            ["7-Zip 26.04 (x64 edition)", "7zip.7zip", "26.04.00.0", "26.05", "winget"],
            ["Node.js", "OpenJS.NodeJS.LTS", "24.15.0", "26.13.0", "winget"],
            ["Ubisoft Connect", "Ubisoft.Connect", "< 175.0.0.13393", "175.0.0.13393", "winget"],
            ["Zoom Workplace", "Zoom.Zoom.EXE", "7.2.1 (48556)", "7.2.5 (51704)", "winget"])
            + "28 upgrades available.\n";

        List<WingetParser.Row> rows = WingetParser.ParseUpgradeTable(output);

        Assert.Equal(4, rows.Count);
        Assert.Equal("7zip.7zip", rows[0].Id);
        Assert.Equal("7-Zip 26.04 (x64 edition)", rows[0].Name);
        Assert.Equal("26.05", rows[0].Available);
        Assert.Equal("< 175.0.0.13393", rows[2].Version);
        Assert.Equal("7.2.5 (51704)", rows[3].Available);
        Assert.All(rows, r => Assert.False(r.Pinned));
    }

    [Fact]
    public void Winget_cut_off_names_parse_but_cut_off_ids_are_dropped()
    {
        string output = Table(
            ["Microsoft Windows Desktop Runtime - 8.0.32 (x64…", "Microsoft.DotNet.DesktopRuntime.8", "8.0.32", "8.0.33", "winget"],
            ["Some Tool", "Vendor.SomeVeryLongPackageIdent…", "1.0", "1.1", "winget"]);

        WingetParser.Row row = Assert.Single(WingetParser.ParseUpgradeTable(output));
        Assert.Equal("Microsoft.DotNet.DesktopRuntime.8", row.Id);
    }

    [Fact]
    public void Winget_pinned_section_is_marked()
    {
        string output = Table(["Git", "Git.Git", "2.55.0.5", "2.56.0.4", "winget"])
            + "1 upgrades available.\n\nThe following packages have an upgrade available, but require explicit targeting for upgrade:\n"
            + Table(["Pinned App", "Vendor.Pinned", "1.0", "2.0", "winget"]);

        List<WingetParser.Row> rows = WingetParser.ParseUpgradeTable(output);

        Assert.Equal(2, rows.Count);
        Assert.False(rows[0].Pinned);
        Assert.True(rows[1].Pinned);
    }

    [Fact]
    public void Winget_table_in_another_language()
    {
        string output = Row("Name", "ID", "Version", "Verfügbar", "Quelle") + "\n" + new string('-', 156) + "\n"
            + Row("Git", "Git.Git", "2.55.0.5", "2.56.0.4", "winget") + "\n"
            + Row("Leere Zeile", "Vendor.NoVersion", "", "", "winget") + "\n"
            + "2 Aktualisierungen verfügbar.\n";

        WingetParser.Row row = Assert.Single(WingetParser.ParseUpgradeTable(output));
        Assert.Equal("Git.Git", row.Id);
        Assert.Equal("2.56.0.4", row.Available);
        Assert.False(row.Pinned);
    }

    [Fact]
    public void Winget_nothing_to_upgrade_is_empty()
    {
        Assert.Empty(WingetParser.ParseUpgradeTable("No installed package found matching input criteria.\n"));
    }

    [Fact]
    public void Winget_versions_list_and_same_family_pick()
    {
        string show = "Found Node.js LTS [OpenJS.NodeJS.LTS]\nVersion\n-------\n26.13.0\n26.12.0\n24.22.1\n24.22.0\n24.15.0\n22.20.0\n";

        List<string> versions = WingetParser.ParseVersions(show);

        Assert.Equal(6, versions.Count);
        Assert.Equal("24.22.1", WingetParser.HighestInFamily(versions, "24.15.0"));
        Assert.Null(WingetParser.HighestInFamily(versions, "26.13.0"));
    }

    [Theory]
    [InlineData(0, "Successfully installed", false, WingetOutcome.Ok)]
    [InlineData(0, "Successfully installed. Restart your PC to finish installation.", false, WingetOutcome.OkReboot)]
    [InlineData(-1978335145, "Unable to remove Portable package as it has been modified; to override this check use --force", true, WingetOutcome.RetryForce)]
    [InlineData(-1978335226, "Starting package install...\nInstaller failed with exit code: 3221225477", true, WingetOutcome.RetryNonAdmin)]
    [InlineData(-1978335226, "Starting package install...\nInstaller failed with exit code: 3221225477", false, WingetOutcome.Failed)]
    [InlineData(-1978335150, "remove: Access is denied.: \"C:\\...\\uv.exe\"\nInstaller failed with exit code: 0x8a150003", true, WingetOutcome.InUse)]
    [InlineData(-1978335189, "No available upgrade found.", true, WingetOutcome.AlreadyCurrent)]
    [InlineData(-1978335189, "Keine anwendbare Aktualisierung gefunden.", true, WingetOutcome.AlreadyCurrent)]
    [InlineData(-1978335145, "Das portable Paket wurde geändert.", true, WingetOutcome.RetryForce)]
    [InlineData(-1978334967, "", true, WingetOutcome.OkReboot)]
    public void Winget_outcomes_seen_on_3_Nov(int exit, string output, bool elevated, WingetOutcome expected)
    {
        Assert.Equal(expected, WingetParser.Classify(exit, output, elevated));
    }

    [Fact]
    public void Winget_upgrade_args()
    {
        var plain = new UpdateItem { Source = UpdateSource.Apps, Id = "Git.Git", Name = "Git" };
        var pinned = new UpdateItem { Source = UpdateSource.Apps, Id = "OpenJS.NodeJS.LTS", Name = "Node.js", TargetVersion = "24.22.1", UnknownVersion = true };

        List<string> a = WingetParser.UpgradeArgs(plain, force: false);
        List<string> b = WingetParser.UpgradeArgs(pinned, force: true);

        Assert.Equal(["upgrade", "--id", "Git.Git", "-e"], a.Take(4));
        Assert.DoesNotContain("--force", a);
        Assert.Contains("--disable-interactivity", a);
        Assert.Equal("24.22.1", b[b.IndexOf("--version") + 1]);
        Assert.Contains("--include-unknown", b);
        Assert.Contains("--force", b);
    }

    [Theory]
    [InlineData("\r   - \r   \\ \rName   Id", "Name   Id")]
    [InlineData("  ██████████▒▒▒▒  1.00 MB / 5.00 MB", "")]
    [InlineData("   -   ", "")]
    [InlineData("Successfully installed\r", "Successfully installed")]
    public void Progress_redraws_are_cleaned(string raw, string expected)
    {
        Assert.Equal(expected, ProcessRunner.CleanLine(raw));
    }

    [Fact]
    public void Windows_update_search_array_object_and_garbage()
    {
        string many = "[{\"id\":\"1d1f9b0e-0000-4000-8000-000000000001\",\"title\":\"Intel - System - 9/19/2017 12:00:00 AM - 11.7.0.1000\",\"driver\":true,\"driverDate\":\"2017-09-19\",\"sizeMb\":0,\"kb\":\"\"},"
                      + "{\"id\":\"1d1f9b0e-0000-4000-8000-000000000002\",\"title\":\"Realtek SoftwareComponent Driver Update (1.0.1050.0)\",\"driver\":true,\"driverDate\":null,\"sizeMb\":4.2,\"kb\":\"\"}]";
        string one = "{\"id\":\"1d1f9b0e-0000-4000-8000-000000000003\",\"title\":\"2026-11 Cumulative Update\",\"driver\":false,\"driverDate\":null,\"sizeMb\":800,\"kb\":\"KB5070001\"}";

        List<WindowsUpdateSource.Record>? a = WindowsUpdateSource.ParseSearch("noise\n" + many);
        List<WindowsUpdateSource.Record>? b = WindowsUpdateSource.ParseSearch(one);

        Assert.NotNull(a);
        Assert.Equal(2, a.Count);
        Assert.Equal(new DateTime(2017, 9, 19), a[0].DriverDate);
        Assert.Null(a[1].DriverDate);
        Assert.Equal(4.2, a[1].SizeMb);
        Assert.Equal("KB5070001", Assert.Single(b!).Kb);
        Assert.Empty(WindowsUpdateSource.ParseSearch("[]")!);
        Assert.Null(WindowsUpdateSource.ParseSearch("Exception from HRESULT: 0x80240438"));
    }

    [Fact]
    public void Windows_update_install_results()
    {
        List<WindowsUpdateSource.InstallResult> r = WindowsUpdateSource.ParseInstall(
            "[{\"id\":\"a\",\"result\":2,\"reboot\":false},{\"id\":\"b\",\"result\":4,\"reboot\":true}]");

        Assert.True(r[0].Succeeded);
        Assert.False(r[1].Succeeded);
        Assert.True(r[1].Reboot);
    }

    [Fact]
    public void Windows_update_install_script_only_takes_guids()
    {
        string script = WindowsUpdateSource.InstallScript(["1d1f9b0e-0000-4000-8000-000000000001", "'; Remove-Item C:\\ -Recurse; '"]);

        Assert.Contains("'1d1f9b0e-0000-4000-8000-000000000001'", script);
        Assert.DoesNotContain("Remove-Item", script);
    }

    [Fact]
    public void Nvidia_driver_status_from_server_sent_events()
    {
        string inner = JsonSerializer.Serialize(new
        {
            systemType = "GEFORCE",
            installedDriver = new { version = "621.01", channel = "GAME_READY" },
            recommendedDrivers = new[] { new { name = "GeForce Game Ready Driver", version = "625.10" } },
        });
        string rpc = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 2, result = new { content = new[] { new { type = "text", text = inner } } } });

        NvidiaSource.DriverStatus? s = NvidiaSource.ParseDriverStatus("event: message\ndata: " + rpc + "\n\n");

        Assert.NotNull(s);
        Assert.Equal("621.01", s.Installed);
        Assert.Equal("GAME_READY", s.Channel);
        Assert.Equal(("625.10", "GeForce Game Ready Driver"), Assert.Single(s.Recommended));
        Assert.NotNull(NvidiaSource.ParseDriverStatus(rpc));
    }

    [Fact]
    public void Nvidia_driver_status_from_structured_content_as_the_app_sends_it()
    {
        // The shape the NVIDIA app returned on 4 Nov: readable text, JSON in structuredContent.
        string body = """
            {"jsonrpc":"2.0","id":2,"result":{"content":[{"type":"text","text":"Here's what I found about your installed NVIDIA GAME_READY driver 621.01:\nsystemType: \"GEFORCE\", installedDriver: {version: \"621.01\"}"}],
            "structuredContent":{"systemType":"GEFORCE","installedDriver":{"version":"621.01","channel":"GAME_READY","availableActions":["reinstall"]},"preferredChannel":"GAME_READY","recommendedDrivers":[],"previouslyInstalledDriver":{"name":"GeForce Game Ready Driver","version":"617.42"}}}}
            """;

        NvidiaSource.DriverStatus? s = NvidiaSource.ParseDriverStatus(body);

        Assert.NotNull(s);
        Assert.Equal("621.01", s.Installed);
        Assert.Empty(s.Recommended);
        Assert.Null(NvidiaSource.ParseDriverStatus("""{"jsonrpc":"2.0","id":2,"result":{"content":[{"type":"text","text":"not json"}]}}"""));
    }

    [Fact]
    public void Pip_check_conflicts()
    {
        List<PythonSource.Conflict> c = PythonSource.ParsePipCheck(
            "kokoro-onnx 0.4.7 has requirement phonemizer-fork==3.3.1, but you have phonemizer-fork 3.3.2.\nNo broken requirements found.\n");

        PythonSource.Conflict only = Assert.Single(c);
        Assert.Equal("kokoro-onnx", only.Package);
        Assert.Equal("phonemizer-fork==3.3.1", only.Requirement);
        Assert.Equal("phonemizer-fork", only.Have);
        Assert.Equal("3.3.2", only.HaveVersion);
    }

    [Fact]
    public void Pip_freeze_is_keyed_by_normalised_name()
    {
        var map = PythonSource.ParseFreeze("huggingface_hub==1.33.0\nfaster-whisper==1.2.1\n-e git+https://x\n# comment==1\n");

        Assert.Equal(("huggingface_hub", "1.33.0"), map["huggingface-hub"]);
        Assert.True(map.ContainsKey("faster-whisper"));
        Assert.Equal(2, map.Count);
    }

    [Fact]
    public void Npm_outdated_ignores_warnings_before_the_json()
    {
        string output = "npm warn config something\n{\n  \"docx\": {\"current\": \"9.8.0\", \"wanted\": \"9.15.2\", \"latest\": \"9.15.2\"},\n  \"rea-agents\": {\"current\": \"6.4.0\", \"wanted\": \"6.7.0\", \"latest\": \"7.0.0\"}\n}\n";

        List<UpdateItem>? items = NodeSource.ParseOutdated(output);

        Assert.NotNull(items);
        Assert.Equal(2, items.Count);
        Assert.False(items[0].IsMajor);
        Assert.True(items[1].IsMajor);
        Assert.Empty(NodeSource.ParseOutdated("")!);
    }

    [Fact]
    public void Rustup_check()
    {
        string output = "stable-x86_64-pc-windows-msvc - update available: 1.99.0 (b940084d7 2026-09-28) -> 1.99.1 (58cf8f9c3 2026-10-13)\n"
                        + "rustup - update available : 1.29.1 -> 1.29.3\n"
                        + "nightly-x86_64-pc-windows-msvc - Up to date : 1.100.0-nightly (abc 2026-11-01)\n";

        List<UpdateItem> items = RustSource.ParseCheck(output);

        Assert.Equal(2, items.Count);
        Assert.Equal("rustup", items[0].Id);
        Assert.Equal("1.29.3", items[0].Available);
        Assert.Equal("stable-x86_64-pc-windows-msvc", items[1].Id);
        Assert.Equal("1.99.0", items[1].Current);
        Assert.Equal("1.99.1", items[1].Available);
    }

    [Theory]
    [InlineData("2.1.317 (Claude Code)", "2.1.317")]
    [InlineData("2.1.318\n", "2.1.318")]
    [InlineData("command not found", null)]
    public void Claude_version(string output, string? expected)
    {
        Assert.Equal(expected, ClaudeCodeSource.ParseVersion(output));
    }

    [Fact]
    public void Smoke_test_output_and_new_failures()
    {
        SmokeTest.Result before = SmokeTest.Parse("PASS pymupdf: 2.1\nPASS whisper gpu: heard it\nFAIL playwright: no browser\n");
        SmokeTest.Result after = SmokeTest.Parse("PASS pymupdf: 2.1\nFAIL whisper gpu: TypeError: open() got an unexpected keyword argument 'metadata_errors'\nFAIL playwright: no browser\nSUMMARY 1 pass, 2 fail\n");

        Assert.Equal(["whisper gpu"], SmokeTest.NewFailures(before, after));
        Assert.Equal(["whisper gpu", "playwright"], SmokeTest.NewFailures(null, after));
        Assert.False(SmokeTest.Parse("python not found").Ran);
    }
}
