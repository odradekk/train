// Train.Tests 进程入口供直接运行测试程序集时派发跨进程模拟夹具。
// 直接运行但未传入 --simulation-process 时返回明确的非零结果。

using Train.Tests.Core.Simulation;

return args.Length > 0 && string.Equals(args[0], "--simulation-process", StringComparison.Ordinal)
    ? await SimulationProcessDriver.RunAsync(args[1..])
    : SimulationProcessDriver.UnknownInvocation(args);
