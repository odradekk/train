using Microsoft.Diagnostics.Runtime;
using System.Text.Json;
using Train.HeapDiagnosis;

try
{
    if (args is ["preflight", var ready, var release])
    {
        Preflight.Run(ready, release);
        return 0;
    }
    if (args is ["analyze-preflight", var dump, var dac, var directory])
    {
        var report = new HeapReport(dump, dac, true);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "preflight.json"), JsonSerializer.Serialize(report, JsonOptions.Value));
        Console.WriteLine("preflight analysis complete");
        return 0;
    }
    if (args is ["analyze-one", var dumpPath, var matchingDac, var output])
    {
        var report = new HeapReport(dumpPath, matchingDac, false);
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "heap.json"), JsonSerializer.Serialize(report, JsonOptions.Value));
        Console.WriteLine("heap analysis complete");
        return 0;
    }
    Console.Error.WriteLine("invalid arguments");
    return 2;
}
catch (Exception error)
{
    Console.Error.WriteLine(error.GetType().FullName);
    return 1;
}

internal static class JsonOptions
{
    internal static readonly JsonSerializerOptions Value = new() { WriteIndented = true };
}
