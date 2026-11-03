using System.Diagnostics;
using System.Text;

namespace SafeUpdater.Core;

public sealed record ProcResult(int ExitCode, string Output, bool TimedOut)
{
    public bool Ok => ExitCode == 0 && !TimedOut;

    public IEnumerable<string> Lines =>
        Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r'));

    public IEnumerable<string> Tail(int count) => Lines.TakeLast(count);
}

/// <summary>Runs a command with UTF-8 output, a timeout, and the PATH as it is now in the registry.</summary>
public static class ProcessRunner
{
    public static async Task<ProcResult> RunAsync(
        string file,
        IEnumerable<string> args,
        TimeSpan timeout,
        Action<string>? onLine = null,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }
        // Installers earlier in the same run can change PATH; children should see the new one.
        psi.Environment["Path"] = Tools.FreshPath();
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";
        psi.Environment["NO_COLOR"] = "1";

        var sb = new StringBuilder();
        object gate = new();
        void Handle(string? data)
        {
            if (data is null)
            {
                return;
            }
            string line = CleanLine(data);
            if (line.Length == 0)
            {
                return;
            }
            lock (gate)
            {
                sb.AppendLine(line);
            }
            onLine?.Invoke(line);
        }

        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => Handle(e.Data);
        p.ErrorDataReceived += (_, e) => Handle(e.Data);
        try
        {
            p.Start();
        }
        catch (Exception ex)
        {
            return new ProcResult(-1, $"Could not start {Path.GetFileName(file)}: {ex.Message}", false);
        }
        p.StandardInput.Close();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        bool timedOut = false;
        try
        {
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            p.WaitForExit(); // lets the async readers drain
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            try
            {
                p.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
        }
        lock (gate)
        {
            if (timedOut)
            {
                sb.AppendLine($"Stopped after {timeout.TotalMinutes:0} minutes without finishing.");
            }
            return new ProcResult(timedOut ? -1 : p.ExitCode, sb.ToString(), timedOut);
        }
    }

    /// <summary>Runs a PowerShell script without quoting trouble (passed as -EncodedCommand).</summary>
    public static Task<ProcResult> PowerShellAsync(string script, TimeSpan timeout, CancellationToken ct = default)
    {
        string wrapped = "[Console]::OutputEncoding = [Text.Encoding]::UTF8\n" + script;
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(wrapped));
        return RunAsync(Tools.PowerShell, ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encoded], timeout, ct: ct);
    }

    /// <summary>winget and pip redraw progress with \r and block characters; keep only the final text of a line.</summary>
    public static string CleanLine(string raw)
    {
        string s = raw.TrimEnd('\r');
        int cr = s.LastIndexOf('\r');
        if (cr >= 0)
        {
            s = s[(cr + 1)..];
        }
        if (s.Any(c => c is >= '▀' and <= '▟'))
        {
            return "";
        }
        string t = s.Trim();
        if (t is "-" or "\\" or "|" or "/")
        {
            return "";
        }
        return s.TrimEnd();
    }
}
