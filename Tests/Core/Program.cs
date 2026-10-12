
using Train.Core.Tests.Simulation;

return args.Length > 0 && string.Equals(args[0], "--simulation-process", StringComparison.Ordinal)
    ? await SimulationProcessDriver.RunAsync(args[1..])
    : SimulationProcessDriver.UnknownInvocation(args);
