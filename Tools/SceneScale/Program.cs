using System.Text.Json;
using Train.SceneScale;

try
{
    var verifyStable = args.Length > 3 && args[^1] == "--verify-stable";
    var argumentCount = args.Length - (verifyStable ? 1 : 0);
    if (argumentCount is < 3 or > 4 || args[1] is not ("bounds" or "stats" or "ready" or "local") ||
        verifyStable && args[1] != "local")
        throw new ArgumentException("参数必须为 <repo> <bounds|stats|ready|local> <save|-> [focus.json] [--verify-stable]；自检仅用于 local。");
    double[]? focus = argumentCount == 4 ? JsonSerializer.Deserialize<double[]>(File.ReadAllText(args[3])) : null;
    if (focus is not null && (focus.Length != 2 || focus.Any(value => !double.IsFinite(value))) ||
        argumentCount == 4 && focus is null || args[1] is "stats" or "local" && focus is null)
        throw new ArgumentException("stats/local 必须提供含两个有限坐标的 focus.json。");
    ProbeProgress.Mode = args[1];
    ProbeProgress.Input = args[2] == "-" ? "new" : "save";
    ProbeProgress.Write("prepare-start");
    var prepared = Composition.Prepare(Path.GetFullPath(args[0]), args[2] == "-" ? null : args[2]);
    ProbeProgress.Write("prepare-complete");
    using var core = prepared.Core;
    return await Probe.Run(args[1], focus, core.Read(), prepared.Content, prepared.Scenes, verifyStable) ? 0 : 1;
}
catch (Exception error)
{
    ProbeProgress.Write("error", new { type = error.GetType().Name,
        methods = new System.Diagnostics.StackTrace(error).GetFrames().Take(12).Select(f => f.GetMethod()?.Name).ToArray() });
    // 异常消息和堆栈可能含用户路径；命令边界只输出错误类别。
    Console.Error.WriteLine($"scene-scale failed: {error.GetType().Name}");
    return 1;
}
