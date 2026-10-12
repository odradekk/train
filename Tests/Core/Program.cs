
using Microsoft.Testing.Platform.Builder;
using Train.Core.Tests;
using Train.Core.Tests.Simulation;

if (args.Length > 0 && string.Equals(args[0], "--simulation-process", StringComparison.Ordinal))
    return await SimulationProcessDriver.RunAsync(args[1..]);

var builder = await TestApplication.CreateBuilderAsync(args);
builder.AddSelfRegisteredExtensions(args);
using var application = await builder.BuildAsync();
return await application.RunAsync();
