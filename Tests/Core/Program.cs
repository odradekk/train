
using Microsoft.Testing.Platform.Builder;
using Train.Core.Tests;
using Train.Core.Tests.Simulation;
using Train.Core.Tests.Sessions;

if (args.Length > 0 && string.Equals(args[0], "--core-process", StringComparison.Ordinal))
    return await CoreProcessDriver.RunAsync(args[1..]);

if (args.Length > 0 && string.Equals(args[0], "--simulation-process", StringComparison.Ordinal))
    return await SimulationProcessDriver.RunAsync(args[1..]);

var builder = await TestApplication.CreateBuilderAsync(args);
builder.AddSelfRegisteredExtensions(args);
using var application = await builder.BuildAsync();
return await application.RunAsync();
