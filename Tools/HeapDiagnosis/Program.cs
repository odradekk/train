using System.Diagnostics;
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
    var locations = new StackTrace(error).GetFrames().Select(frame => frame.GetMethod())
        .Where(method => method?.DeclaringType is not null)
        .Select(method =>
        {
            var type = method!.DeclaringType!;
            return (type.IsGenericType ? type.GetGenericTypeDefinition() : type).FullName + "." + method.Name;
        }).ToArray();
    Console.Error.WriteLine("DIAGNOSTIC_FAILURE " + JsonSerializer.Serialize(new
    {
        exception_type = error.GetType().FullName,
        // 固定的 ClrMD 4.2 不公开 Kind；不能把 HResult 当作 Kind。
        kind = (string?)null,
        kind_available = false,
        hresult = $"0x{error.HResult:X8}",
        locations
    }));
    return 1;
}

internal static class JsonOptions
{
    internal static readonly JsonSerializerOptions Value = new() { WriteIndented = true };
}
