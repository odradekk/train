using Microsoft.Testing.Platform.Builder;
using Train.Godot.Tests;
using Train.Godot.Tests.Authoring;

if (args.Length > 0 && args[0] == "--package-write-process")
    return PackageFilesTests.WriteInChild(args[1], int.Parse(args[2]));

var builder = await TestApplication.CreateBuilderAsync(args);
builder.AddSelfRegisteredExtensions(args);
using var application = await builder.BuildAsync();
return await application.RunAsync();
