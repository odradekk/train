// Train.Tests 进程入口只派发跨进程模拟夹具。
// 普通 MSTest 发现不带该参数时返回明确的非零结果。

using Train.Tests.Core.Simulation;

return args.Length > 0 && string.Equals(args[0], "--simulation-process", StringComparison.Ordinal)
    ? await SimulationProcessDriver.RunAsync(args[1..])
    : SimulationProcessDriver.UnknownInvocation(args);
