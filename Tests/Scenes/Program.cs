using Microsoft.Testing.Platform.Builder;
using Train.Scenes.Tests;

if (args.Length > 0 && args[0] is "--node-process" or "--empty-node-switch-process")
{
    try
    {
        if (args[0] == "--node-process") await NodeSceneTests.ObserveProductionNode(args[1]);
        else await NodeSwitchTests.ObserveProductionEmptyNodeSwitch(args[1]);
        return 0;
    }
    catch (Exception error)
    {
        Console.Error.WriteLine(error);
        return 1;
    }
}

var builder = await TestApplication.CreateBuilderAsync(args);
builder.AddSelfRegisteredExtensions(args);
using var application = await builder.BuildAsync();
return await application.RunAsync();
