
using Microsoft.Testing.Platform.Builder;
using Train.Core.Tests;
using Train.Core.Tests.Sessions;
using Train.Core.Tests.Railway;
using Train.Core.Tests.Inventory;
using Train.Core.Tests.Settlements;
using Train.Core.Tests.Production;

if (args.Length > 0 && string.Equals(args[0], "--population-process", StringComparison.Ordinal))
    return await PopulationProcessDriver.RunAsync(args[1..]);

if (args.Length > 0 && string.Equals(args[0], "--core-process", StringComparison.Ordinal))
    return await CoreProcessDriver.RunAsync(args[1..]);

if (args.Length > 0 && string.Equals(args[0], "--train-process", StringComparison.Ordinal))
    return await TrainProcessDriver.RunAsync(args[1..]);

if (args.Length > 0 && string.Equals(args[0], "--dispatch-process", StringComparison.Ordinal))
    return await TrainDispatchProcessDriver.RunAsync(args[1..]);

if (args.Length > 0 && string.Equals(args[0], "--inventory-process", StringComparison.Ordinal))
    return await InventoryProcessDriver.RunAsync(args[1..]);

if (args.Length > 0 && string.Equals(args[0], "--production-process", StringComparison.Ordinal))
    return await ProductionProcessDriver.RunAsync(args[1..]);

if (args.Length > 0 && string.Equals(args[0], "--extraction-process", StringComparison.Ordinal))
    return await ExtractionProcessDriver.RunAsync(args[1..]);

var builder = await TestApplication.CreateBuilderAsync(args);
builder.AddSelfRegisteredExtensions(args);
using var application = await builder.BuildAsync();
return await application.RunAsync();
