using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SafeUpdater.Core;

/// <summary>Everything the app writes lives in %LOCALAPPDATA%\SafeUpdater.</summary>
public static class AppPaths
{
    public static string Root { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SafeUpdater");

    public static string Logs => Ensure(Path.Combine(Root, "logs"));
    public static string Snapshots => Ensure(Path.Combine(Root, "snapshots"));
    public static string Jobs => Ensure(Path.Combine(Root, "jobs"));
    public static string RunsCsv => Path.Combine(Ensure(Root), "runs.csv");
    public static string ErrorLog => Path.Combine(Ensure(Root), "error.log");
    public static string SettingsFile => Path.Combine(Ensure(Root), "settings.json");

    private static string Ensure(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }
}

public static class Json
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };
}

public sealed class Settings
{
    public bool IncludeMajors { get; set; }
    public bool TestAfterUpdate { get; set; } = true;
    public bool RestorePoint { get; set; } = true;
    public DateTime? LastChecked { get; set; }
    public DateTime? LastUpdated { get; set; }

    /// <summary>The user-site snapshot; the matching "-env" file, when present, holds the interpreter's own packages.</summary>
    public string? LastPipSnapshot { get; set; }

    /// <summary>Updates the user asked not to see again, as "Source|Id".</summary>
    public HashSet<string> Ignored { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static Settings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(AppPaths.SettingsFile), Json.Options) ?? new Settings();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            RunLog.WriteError("settings.json unreadable, using defaults: " + ex.Message);
        }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(this, Json.Options));
        }
        catch (IOException ex)
        {
            RunLog.WriteError("Could not save settings: " + ex.Message);
        }
    }
}

public static class RunLog
{
    private static readonly object Gate = new();

    public static string NewRunLog(string stamp) => Path.Combine(AppPaths.Logs, $"run-{stamp}.log");

    public static void Append(string path, string text)
    {
        lock (Gate)
        {
            try
            {
                File.AppendAllText(path, text.EndsWith('\n') ? text : text + Environment.NewLine, Encoding.UTF8);
            }
            catch (IOException)
            {
            }
        }
    }

    public static void WriteError(string text) =>
        Append(AppPaths.ErrorLog, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {text}");

    /// <summary>One line per item per run in runs.csv, so "what changed on 3 Nov" has an answer.</summary>
    public static void Record(UpdateItem item, DateTime when)
    {
        bool fresh = !File.Exists(AppPaths.RunsCsv);
        var sb = new StringBuilder();
        if (fresh)
        {
            sb.AppendLine("when,source,id,name,from,to,status,result");
        }
        sb.AppendLine(string.Join(',', new[]
        {
            when.ToString("yyyy-MM-dd HH:mm:ss"), item.Source.ToString(), item.Id, item.Name, item.Current,
            item.TargetVersion ?? item.Available, item.Status.ToString(), item.Result ?? "",
        }.Select(Csv)));
        Append(AppPaths.RunsCsv, sb.ToString());
    }

    private static string Csv(string v) => v.IndexOfAny([',', '"', '\n']) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
}
