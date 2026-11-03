using System.Globalization;
using System.Text.Json;

namespace SafeUpdater.Core;

/// <summary>Windows Update through its COM API, driven from PowerShell. Searching works without admin; installing needs it.</summary>
public static class WindowsUpdateSource
{
    public sealed record Record(string Id, string Title, bool Driver, DateTime? DriverDate, double SizeMb, string Kb,
        DateTime? InstalledDate = null, bool Optional = false);

    /// <summary>
    /// Lists offered updates. For each driver it finds the device by hardware id and reads the date of the driver
    /// already installed on it, so an offered driver can be checked as really newer. "optional" is Windows' own
    /// flag (not auto-selected), so this works in any display language.
    /// </summary>
    internal const string SearchScript = """
        $ErrorActionPreference = 'Stop'
        $s = New-Object -ComObject Microsoft.Update.Session
        $q = $s.CreateUpdateSearcher()
        $r = $q.Search("IsInstalled=0 and IsHidden=0")
        $devices = $null
        $list = foreach ($u in $r.Updates) {
            $isDriver = $u.Type -eq 2
            $date = $null; $installed = $null
            if ($isDriver) {
                try { $date = $u.DriverVerDate.ToString('yyyy-MM-dd') } catch { }
                try {
                    if ($null -eq $devices) {
                        $byDevice = @{}
                        foreach ($d in Get-CimInstance Win32_PnPSignedDriver) { if ($d.DeviceID) { $byDevice[$d.DeviceID] = $d } }
                        $devices = foreach ($e in Get-CimInstance Win32_PnPEntity) {
                            [pscustomobject]@{ ids = @($e.HardwareID) + @($e.CompatibleID); driver = $byDevice[$e.DeviceID] }
                        }
                    }
                    $hw = $u.DriverHardwareID
                    $match = $devices | Where-Object { $_.driver -and $_.driver.DriverDate -and ($_.ids -contains $hw) } | Select-Object -First 1
                    if ($match) { $installed = ([datetime]$match.driver.DriverDate).ToString('yyyy-MM-dd') }
                } catch { }
            }
            [pscustomobject]@{
                id = $u.Identity.UpdateID; title = $u.Title; driver = $isDriver; driverDate = $date; installedDate = $installed
                optional = -not $u.AutoSelectOnWebSites
                sizeMb = [math]::Round($u.MaxDownloadSize / 1MB, 1); kb = (@($u.KBArticleIDs) | ForEach-Object { "KB$_" }) -join ', '
            }
        }
        ConvertTo-Json -InputObject @($list) -Compress -Depth 3
        """;

    internal const string StoreScanScript = """
        $ErrorActionPreference = 'Stop'
        $r = Get-CimInstance -Namespace 'root\cimv2\mdm\dmmap' -ClassName 'MDM_EnterpriseModernAppManagement_AppManagement01' |
            Invoke-CimMethod -MethodName UpdateScanMethod
        "ReturnValue=$($r.ReturnValue)"
        """;

    public static async Task<SourceResult> CheckAsync(CancellationToken ct)
    {
        ProcResult r = await ProcessRunner.PowerShellAsync(SearchScript, TimeSpan.FromMinutes(6), ct);
        List<Record>? records = ParseSearch(r.Output);
        var items = new List<UpdateItem>();
        if (records is null)
        {
            items.Add(StoreItem());
            return new SourceResult
            {
                Source = UpdateSource.Windows,
                Items = items,
                Failed = true,
                Message = "Windows Update could not be searched: " + r.Tail(1).FirstOrDefault(),
            };
        }
        foreach (Record rec in records)
        {
            items.Add(new UpdateItem
            {
                Source = UpdateSource.Windows,
                Kind = rec.Driver ? ItemKind.Driver : ItemKind.WindowsUpdate,
                Id = rec.Id,
                Name = rec.Title,
                Current = rec.Driver ? "Driver" : "",
                Available = rec.SizeMb > 0 ? $"{rec.SizeMb:0.#} MB" : "",
                DriverDate = rec.DriverDate,
                InstalledDriverDate = rec.InstalledDate,
                Optional = rec.Optional,
                Detail = rec.Kb,
                NeedsAdmin = true,
            });
        }
        items.Add(StoreItem());
        return new SourceResult
        {
            Source = UpdateSource.Windows,
            Items = items,
            Message = records.Count == 0 ? "Windows is up to date" : null,
        };
    }

    private static UpdateItem StoreItem() => new()
    {
        Source = UpdateSource.Windows,
        Kind = ItemKind.StoreScan,
        Id = "store-scan",
        Name = "Microsoft Store apps",
        Available = "Ask the Store to update",
        NeedsAdmin = true,
    };

    /// <summary>The JSON line from <see cref="SearchScript"/>; null when the search failed.</summary>
    public static List<Record>? ParseSearch(string output)
    {
        string? json = output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith('[') || l.StartsWith('{'));
        if (json is null)
        {
            return null;
        }
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            IEnumerable<JsonElement> elems = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray()
                : [doc.RootElement];
            var list = new List<Record>();
            foreach (JsonElement e in elems)
            {
                if (e.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                list.Add(new Record(
                    Str(e, "id"), Str(e, "title"),
                    e.TryGetProperty("driver", out JsonElement dr) && dr.ValueKind == JsonValueKind.True,
                    Date(e, "driverDate"),
                    e.TryGetProperty("sizeMb", out JsonElement sz) && sz.ValueKind == JsonValueKind.Number ? sz.GetDouble() : 0,
                    Str(e, "kb"),
                    Date(e, "installedDate"),
                    e.TryGetProperty("optional", out JsonElement op) && op.ValueKind == JsonValueKind.True));
            }
            return list;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Installs the chosen updates by id and prints one JSON line of results.</summary>
    public static string InstallScript(IEnumerable<string> ids)
    {
        string list = string.Join(",", ids.Where(IsGuid).Select(i => $"'{i}'"));
        return $$"""
            $ErrorActionPreference = 'Stop'
            $ids = @({{list}})
            $s = New-Object -ComObject Microsoft.Update.Session
            $r = $s.CreateUpdateSearcher().Search("IsInstalled=0 and IsHidden=0")
            $col = New-Object -ComObject Microsoft.Update.UpdateColl
            foreach ($u in $r.Updates) {
                if ($ids -contains $u.Identity.UpdateID) {
                    if (-not $u.EulaAccepted) { $u.AcceptEula() }
                    [void]$col.Add($u)
                }
            }
            $out = @()
            if ($col.Count -gt 0) {
                $d = $s.CreateUpdateDownloader(); $d.Updates = $col; [void]$d.Download()
                $i = $s.CreateUpdateInstaller(); $i.Updates = $col; $res = $i.Install()
                for ($k = 0; $k -lt $col.Count; $k++) {
                    $out += [pscustomobject]@{ id = $col.Item($k).Identity.UpdateID; result = $res.GetUpdateResult($k).ResultCode; reboot = $res.RebootRequired }
                }
            }
            ConvertTo-Json -InputObject @($out) -Compress
            """;
    }

    public sealed record InstallResult(string Id, int ResultCode, bool Reboot)
    {
        /// <summary>2 = succeeded, 3 = succeeded with errors.</summary>
        public bool Succeeded => ResultCode is 2 or 3;
    }

    public static List<InstallResult> ParseInstall(string output)
    {
        string? json = output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith('[') || l.StartsWith('{'));
        var list = new List<InstallResult>();
        if (json is null)
        {
            return list;
        }
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            IEnumerable<JsonElement> elems = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray() : [doc.RootElement];
            foreach (JsonElement e in elems.Where(e => e.ValueKind == JsonValueKind.Object))
            {
                list.Add(new InstallResult(
                    Str(e, "id"),
                    e.TryGetProperty("result", out JsonElement rc) && rc.ValueKind == JsonValueKind.Number ? rc.GetInt32() : 0,
                    e.TryGetProperty("reboot", out JsonElement rb) && rb.ValueKind == JsonValueKind.True));
            }
        }
        catch (JsonException)
        {
        }
        return list;
    }

    private static bool IsGuid(string s) => Guid.TryParse(s, out _);

    private static DateTime? Date(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement d) && d.ValueKind == JsonValueKind.String
        && DateTime.TryParseExact(d.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsed)
            ? parsed
            : null;

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
