namespace SafeUpdater.Core;

/// <summary>
/// Runs Assets\smoke-test.py: PDF, Word, PowerPoint, Excel, Playwright and GPU Whisper. Run before and after a
/// Python update; a test that passed before and fails after means the update broke something (av 19, 3 Nov).
/// </summary>
public static class SmokeTest
{
    public sealed record Result(bool Ran, List<string> Passed, List<string> Failed, string Raw);

    private const string SpeechScript = """
        Add-Type -AssemblyName System.Speech
        $t = New-Object System.Speech.Synthesis.SpeechSynthesizer
        $t.SetOutputToWaveFile($env:SU_WAV)
        $t.Speak('The quick brown fox jumps over the lazy dog.')
        $t.Dispose()
        """;

    public static async Task<Result> RunAsync(string python, string script, CancellationToken ct)
    {
        if (!File.Exists(script))
        {
            return new Result(false, [], [], "smoke-test.py is missing next to the app");
        }
        string wav = Path.Combine(AppPaths.Root, "smoke-speech.wav");
        if (!File.Exists(wav))
        {
            Environment.SetEnvironmentVariable("SU_WAV", wav);
            await ProcessRunner.PowerShellAsync(SpeechScript, TimeSpan.FromMinutes(1), ct);
        }
        ProcResult r = await ProcessRunner.RunAsync(python, ["-W", "ignore", script, wav], TimeSpan.FromMinutes(8), ct: ct);
        return Parse(r.Output);
    }

    public static Result Parse(string output)
    {
        var passed = new List<string>();
        var failed = new List<string>();
        foreach (string line in output.Split('\n').Select(l => l.Trim()))
        {
            if (line.StartsWith("PASS ", StringComparison.Ordinal) || line.StartsWith("FAIL ", StringComparison.Ordinal))
            {
                int colon = line.IndexOf(':');
                string name = colon > 5 ? line[5..colon] : line[5..];
                (line.StartsWith('P') ? passed : failed).Add(name);
            }
        }
        return new Result(passed.Count + failed.Count > 0, passed, failed, output);
    }

    /// <summary>Tests that passed before and fail now. Without a baseline, every failure counts.</summary>
    public static List<string> NewFailures(Result? before, Result after) =>
        after.Failed.Where(f => before is null || !before.Ran || before.Passed.Contains(f)).ToList();
}
