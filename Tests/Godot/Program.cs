using Microsoft.Testing.Platform.Builder;
using Train.Godot.Tests;
using Train.Godot.Tests.Authoring;
using Train.Godot.Tests.Measurement;
using Train.Godot.Tests.Recording;
using Train.Godot.Tests.Saves;

if (args.Length > 0 && args[0] == "--package-write-process")
    return PackageFilesTests.WriteInChild(args[1], int.Parse(args[2]));
if (args.Length > 0 && args[0] == "--save-container-memory")
    return SaveContainerMemoryTests.MeasureInChild(args[1], int.Parse(args[2]));

if (args.Length > 0 && args[0] == "--recording-package-memory")
    return RecordingPackageMemoryTests.MeasureInChild(args[1], int.Parse(args[2]));

if (args.Length > 0 && args[0] == "--array-pool-diagnosis")
    return ArrayPoolDiagnosisTests.RunChild(args[1], bool.Parse(args[2]));

var builder = await TestApplication.CreateBuilderAsync(args);
builder.AddSelfRegisteredExtensions(args);
using var application = await builder.BuildAsync();
return await application.RunAsync();
