using System.Diagnostics;
using System.Text.Json;

namespace Train.ContractChecks;

internal static class ToolProcess
{
    internal static async Task<string> Run(string program, IEnumerable<string> arguments,
        string directory, string log, int timeoutSeconds = 120)
    {
        var info = new ProcessStartInfo(program)
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        info.Environment["UseSharedCompilation"] = "false";
        info.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        using var process = new Process { StartInfo = info };
        var watch = Stopwatch.StartNew();
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        var timedOut = false;
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            timedOut = true;
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
        var output = await stdout;
        var errors = await stderr;
        watch.Stop();
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        await File.WriteAllTextAsync(log, JsonSerializer.Serialize(new
        {
            program, arguments = info.ArgumentList, directory, timeoutSeconds,
            exitCode = process.ExitCode, timedOut, seconds = watch.Elapsed.TotalSeconds
        }) + Environment.NewLine + output + errors);
        if (timedOut || process.ExitCode != 0)
            throw new InvalidOperationException($"process failed: {program} (exit={process.ExitCode}, timeout={timedOut}); log={log}\n{output}{errors}");
        return output;
    }
}
