using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SafeUpdater.Core;

/// <summary>
/// Reads the GeForce driver status from the NVIDIA app's local MCP server (http://127.0.0.1:13508/mcp).
/// The NVIDIA app does not let other programs install drivers, so a newer driver is shown with a link to the app.
/// </summary>
public static class NvidiaSource
{
    private const string Url = "http://127.0.0.1:13508/mcp";

    public sealed record DriverStatus(string Installed, string Channel, List<(string Version, string Name)> Recommended);

    public static async Task<SourceResult> CheckAsync(CancellationToken ct)
    {
        // No NVIDIA card: nothing to show. (AMD and Intel drivers come through Windows Update.)
        ProcResult gpu = await ProcessRunner.PowerShellAsync("(Get-CimInstance Win32_VideoController).Name -join '|'", TimeSpan.FromSeconds(40), ct);
        if (gpu.Ok && !gpu.Output.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
        {
            return new SourceResult { Source = UpdateSource.Nvidia, Absent = true };
        }
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            (string body, string? session) = await PostAsync(http, null, new
            {
                jsonrpc = "2.0", id = 1, method = "initialize",
                @params = new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "SafeUpdater", version = "0.1.0" } },
            }, ct);
            await PostAsync(http, session, new { jsonrpc = "2.0", method = "notifications/initialized" }, ct);
            (body, _) = await PostAsync(http, session, new
            {
                jsonrpc = "2.0", id = 2, method = "tools/call",
                @params = new { name = "nvapp_client_get_driver_status", arguments = new { } },
            }, ct);
            DriverStatus? status = ParseDriverStatus(body);
            if (status is null)
            {
                return new SourceResult { Source = UpdateSource.Nvidia, Message = "The NVIDIA app answered, but not with a driver status." };
            }
            var items = status.Recommended
                .Where(r => Versions.Compare(r.Version, status.Installed) > 0)
                .Select(r => new UpdateItem
                {
                    Source = UpdateSource.Nvidia,
                    Kind = ItemKind.Info,
                    Id = "nvidia-driver-" + r.Version,
                    Name = string.IsNullOrEmpty(r.Name) ? "GeForce driver" : r.Name,
                    Current = status.Installed,
                    Available = r.Version,
                    Selectable = false,
                })
                .ToList();
            return new SourceResult
            {
                Source = UpdateSource.Nvidia,
                Items = items,
                Message = items.Count == 0 ? $"Driver {status.Installed} ({Pretty(status.Channel)}) is the newest" : null,
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new SourceResult { Source = UpdateSource.Nvidia, Message = "The NVIDIA app isn't open (or isn't installed), so the graphics driver wasn't checked. Open it and check again." };
        }
    }

    private static async Task<(string Body, string? Session)> PostAsync(HttpClient http, string? session, object payload, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, Url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (session is not null)
        {
            req.Headers.Add("Mcp-Session-Id", session);
        }
        using HttpResponseMessage res = await http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        string? id = res.Headers.TryGetValues("Mcp-Session-Id", out IEnumerable<string>? v) ? v.FirstOrDefault() : session;
        return (await res.Content.ReadAsStringAsync(ct), id);
    }

    /// <summary>The JSON-RPC message in a response body, plain or as server-sent events ("data: {...}").</summary>
    internal static string? ExtractJson(string body)
    {
        string t = body.Trim();
        if (t.StartsWith('{'))
        {
            return t;
        }
        return body.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("data:", StringComparison.Ordinal))
            .Select(l => l[5..].Trim())
            .LastOrDefault(l => l.StartsWith('{'));
    }

    internal static DriverStatus? ParseDriverStatus(string body)
    {
        string? json = ExtractJson(body);
        if (json is null)
        {
            return null;
        }
        try
        {
            using JsonDocument rpc = JsonDocument.Parse(json);
            if (!rpc.RootElement.TryGetProperty("result", out JsonElement result))
            {
                return null;
            }
            // The NVIDIA app sends the data in structuredContent; its text content is a readable summary, not JSON.
            if (result.TryGetProperty("structuredContent", out JsonElement structured) && structured.ValueKind == JsonValueKind.Object)
            {
                return FromStatus(structured);
            }
            if (result.TryGetProperty("content", out JsonElement content) && content.ValueKind == JsonValueKind.Array
                && content.GetArrayLength() > 0 && content[0].TryGetProperty("text", out JsonElement text))
            {
                using JsonDocument doc = JsonDocument.Parse(text.GetString() ?? "{}");
                return FromStatus(doc.RootElement);
            }
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static DriverStatus? FromStatus(JsonElement root)
    {
        if (!root.TryGetProperty("installedDriver", out JsonElement inst))
        {
            return null;
        }
        var recommended = new List<(string, string)>();
        if (root.TryGetProperty("recommendedDrivers", out JsonElement recs) && recs.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement r in recs.EnumerateArray())
            {
                recommended.Add((Str(r, "version"), Str(r, "name")));
            }
        }
        return new DriverStatus(Str(inst, "version"), Str(inst, "channel"), recommended);
    }

    /// <summary>Starts the NVIDIA app if it is installed in the usual place.</summary>
    public static bool OpenApp()
    {
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        foreach (string exe in new[]
                 {
                     Path.Combine(pf, "NVIDIA Corporation", "NVIDIA App", "CEF", "NVIDIA App.exe"),
                     Path.Combine(pf, "NVIDIA Corporation", "NVIDIA app", "CEF", "NVIDIA app.exe"),
                 })
        {
            if (File.Exists(exe))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true });
                return true;
            }
        }
        return false;
    }

    private static string Pretty(string channel) => channel.Replace('_', ' ').ToLowerInvariant() switch
    {
        "game ready" => "Game Ready",
        "studio" => "Studio",
        var other => other,
    };

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
