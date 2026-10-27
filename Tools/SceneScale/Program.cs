using Train.SceneScale;

try
{
    if (args.Length != 3 || args[1] is not ("bounds" or "stats" or "ready" or "local"))
        throw new ArgumentException("参数必须为 <repo> <bounds|stats|ready|local> <save|->。");
    ProbeProgress.Mode = args[1];
    ProbeProgress.Input = args[2] == "-" ? "new" : "save";
    ProbeProgress.Write("prepare-start");
    var prepared = Composition.Prepare(Path.GetFullPath(args[0]), args[2] == "-" ? null : args[2]);
    ProbeProgress.Write("prepare-complete");
    using var core = prepared.Core;
    return await Probe.Run(args[1], core.Read(), prepared.Content, prepared.Scenes) ? 0 : 1;
}
catch (Exception error)
{
    ProbeProgress.Write("error", new { type = error.GetType().Name,
        methods = new System.Diagnostics.StackTrace(error).GetFrames().Take(12).Select(f => f.GetMethod()?.Name).ToArray() });
    // 异常消息和堆栈可能含用户路径；命令边界只输出错误类别。
    Console.Error.WriteLine($"scene-scale failed: {error.GetType().Name}");
    return 1;
}
