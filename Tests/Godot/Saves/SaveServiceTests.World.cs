using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App.Saves;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Godot.Tests.Host;

namespace Train.Godot.Tests.Saves;

public sealed partial class SaveServiceTests
{
    [TestMethod]
    public void World_metadata_survives_new_game_save_restore_save_as_and_automatic_save()
    {
        using var run = new Run();
        var world = new SaveWorld("fixture:chosen-profile", 43);
        StartWorld(run, world);
        Assert.IsNull(run.Service.Save());
        run.Drain();
        var first = run.Service.List().Single();
        Assert.AreEqual(world, first.Header.World);

        run.ConfigureRestore();
        Assert.IsNull(run.Service.Load(first));
        run.Drain();
        Assert.IsNull(run.Service.Save());
        run.Drain();
        Assert.AreEqual(world, run.Store.ReadHeader(first.Path).World);

        Assert.IsNull(run.Service.SaveAs("另一条线路"));
        run.Drain();
        var second = run.Service.List().Single(entry => entry.Header.Id == run.Service.Status!.Identity.Id);
        Assert.AreNotEqual(first.Header.Id, second.Header.Id);
        Assert.AreEqual(world, second.Header.World);

        run.Service.SetAutoSaveInterval(TimeSpan.FromSeconds(1));
        run.Now = 2;
        run.Process();
        run.Drain();
        Assert.AreEqual(world, run.Service.List().Single(entry => entry.Automatic).Header.World);
    }

    [TestMethod]
    public void Loading_an_unrecorded_world_clears_the_previous_games_world_metadata()
    {
        using var run = new Run();
        var world = new SaveWorld("fixture:chosen-profile", 43);
        StartWorld(run, world);
        Assert.IsNull(run.Service.Save());
        run.Drain();
        Assert.AreEqual(world, run.Service.List().Single().Header.World);
        var source = run.Source();
        run.ConfigureRestore();
        Assert.IsNull(run.Service.Load(new(source, run.Store.ReadHeader(source), false)));
        run.Drain();
        Assert.IsNull(run.Service.Save());
        run.Drain();
        Assert.IsNull(run.Store.ReadHeader(run.Store.PathFor(run.Id)).World);
    }

    [TestMethod]
    public void Import_and_selected_file_export_keep_recorded_world_metadata()
    {
        using var run = new Run();
        var world = new SaveWorld("fixture:imported-profile", ulong.MaxValue);
        var source = run.Source();
        var document = run.Store.Read(source);
        File.WriteAllText(source, SaveContainer.Encode(document with { Header = document.Header with { World = world } }));
        run.ConfigureRestore();
        Assert.IsNull(run.Service.Import(source));
        run.Drain();
        var imported = run.Service.List().Single();
        Assert.AreEqual(world, imported.Header.World);
        var exported = Path.Combine(run.Root, "export.json");
        Assert.IsNull(run.Service.Export(imported, exported));
        run.Drain();
        CollectionAssert.AreEqual(File.ReadAllBytes(imported.Path), File.ReadAllBytes(exported));
    }

    private static void StartWorld(Run run, SaveWorld world)
    {
        var prepared = run.ConfigureRestore();
        run.Host.World = (_, _, _, _) => new ContentResult<WorldGenerationInput>.Success(HostCoreScenario.World);
        run.Host.Copies = (view, _) => view;
        run.Host.Core.PrepareNewCall = (_, _, _) => new CoreResult<ICoreSessionCandidate>.Success(prepared.Core);
        Assert.IsNull(run.Host.Host.BeginNewGame(new(world.GenerationProfileId), world.Seed, "fixed", HostFixture.Options).Problem);
        run.Host.PumpUntil(() => !run.Host.Host.IsBusy);
    }
}
