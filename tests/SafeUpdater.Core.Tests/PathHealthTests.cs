using SafeUpdater.Core;

namespace SafeUpdater.Core.Tests;

/// <summary>The 3 Nov FFmpeg breakage, rebuilt in a temp folder.</summary>
public sealed class PathHealthTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "su-path-" + Guid.NewGuid().ToString("N"));
    private readonly string _links;
    private readonly string _packages;

    public PathHealthTests()
    {
        _links = Path.Combine(_root, "Links");
        _packages = Path.Combine(_root, "Packages");
        Directory.CreateDirectory(_links);
        Directory.CreateDirectory(_packages);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Exe(string relDir, string name)
    {
        string dir = Path.Combine(_packages, relDir);
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, name);
        File.WriteAllText(file, "x");
        return file;
    }

    [Fact]
    public void A_lost_link_whose_target_still_exists_is_recreated()
    {
        string ffmpeg = Exe(@"Gyan.FFmpeg_Microsoft.Winget.Source_8wekyb3d8bbwe\ffmpeg-9.0.2-full_build\bin", "ffmpeg.exe");
        var before = new PathHealth.Snapshot { Links = { ["ffmpeg.exe"] = ffmpeg } };

        PathHealth.Repair r = Assert.Single(PathHealth.Plan(before, [], _links, _packages));

        Assert.Equal("link", r.Kind);
        Assert.Equal(Path.Combine(_links, "ffmpeg.exe"), r.Path);
        Assert.Equal(ffmpeg, r.Target);
    }

    [Fact]
    public void A_lost_link_follows_the_package_to_its_new_version_folder()
    {
        string gone = Path.Combine(_packages, @"Tool.X_Microsoft.Winget.Source_8wekyb3d8bbwe\tool-1.0\bin\tool.exe");
        string now = Exe(@"Tool.X_Microsoft.Winget.Source_8wekyb3d8bbwe\tool-2.0\bin", "tool.exe");
        var before = new PathHealth.Snapshot { Links = { ["tool.exe"] = gone } };

        PathHealth.Repair r = Assert.Single(PathHealth.Plan(before, [], _links, _packages));

        Assert.Equal(now, r.Target);
    }

    [Fact]
    public void A_dead_path_entry_moves_to_the_folder_with_the_same_programs()
    {
        string oldDir = Path.Combine(_packages, @"yt-dlp.FFmpeg_Microsoft.Winget.Source_8wekyb3d8bbwe\ffmpeg-N-125365-win64-gpl\bin");
        Exe(@"yt-dlp.FFmpeg_Microsoft.Winget.Source_8wekyb3d8bbwe\ffmpeg-N-127043-win64-gpl\bin", "ffmpeg.exe");
        string newProbe = Exe(@"yt-dlp.FFmpeg_Microsoft.Winget.Source_8wekyb3d8bbwe\ffmpeg-N-127043-win64-gpl\bin", "ffprobe.exe");
        var before = new PathHealth.Snapshot { PackageDirs = { [oldDir] = ["ffmpeg.exe", "ffprobe.exe"] } };

        PathHealth.Repair r = Assert.Single(PathHealth.Plan(before, [oldDir], _links, _packages));

        Assert.Equal("path", r.Kind);
        Assert.Equal(Path.GetDirectoryName(newProbe), r.Target);
        Assert.Contains("yt-dlp.FFmpeg", r.Description);
    }

    [Fact]
    public void Nothing_to_do_when_everything_is_still_there()
    {
        string bin = Path.GetDirectoryName(Exe(@"Jq_Microsoft.Winget.Source_8wekyb3d8bbwe", "jq.exe"))!;
        File.WriteAllText(Path.Combine(_links, "rg.exe"), "x");
        var before = new PathHealth.Snapshot
        {
            Links = { ["rg.exe"] = Path.Combine(_links, "rg.exe") },
            PackageDirs = { [bin] = ["jq.exe"] },
        };

        Assert.Empty(PathHealth.Plan(before, [bin], _links, _packages));
    }

    [Fact]
    public void Snapshot_records_package_dirs_on_path()
    {
        string bin = Path.GetDirectoryName(Exe(@"Poppler_Microsoft.Winget.Source_8wekyb3d8bbwe\Library\bin", "pdftoppm.exe"))!;

        PathHealth.Snapshot s = PathHealth.Take([bin, @"C:\Windows\System32"], _links, _packages);

        Assert.Equal(["pdftoppm.exe"], s.PackageDirs[bin]);
        Assert.Single(s.PackageDirs);
    }
}

public class ProgressReaderTests
{
    [Fact]
    public void Reads_complete_lines_only_and_picks_up_where_it_left_off()
    {
        string file = Path.Combine(Path.GetTempPath(), "su-progress-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            var reader = new ProgressReader(file);
            Assert.Empty(reader.ReadNew());

            File.AppendAllText(file, "{\"key\":\"Apps|Git.Git\",\"status\":\"Running\"}\n{\"key\":\"Apps|Git.Git\",\"sta");
            ProgressEvent first = Assert.Single(reader.ReadNew());
            Assert.Equal(ItemStatus.Running, first.Status);

            File.AppendAllText(file, "tus\":\"Done\",\"message\":\"Updated\"}\n{\"finished\":true}\n");
            List<ProgressEvent> next = reader.ReadNew();
            Assert.Equal(2, next.Count);
            Assert.Equal("Updated", next[0].Message);
            Assert.True(next[1].Finished);
        }
        finally
        {
            File.Delete(file);
        }
    }
}
