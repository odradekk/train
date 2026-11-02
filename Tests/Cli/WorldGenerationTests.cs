using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content;
using Train.Content.Contracts;
using Train.Core;
using Train.Core.Contracts;

namespace Train.Cli.Tests;

[TestClass]
[TestCategory("integration")]
public sealed class WorldGenerationTests
{
    private static IContentCandidate _content = null!;

    [ClassInitialize]
    public static void LoadContent(TestContext _)
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "global.json")))
            repository = repository.Parent;
        Assert.IsNotNull(repository);
        _content = Composition.Create().Load(CliRunner.Installation(repository.FullName),
            new ContentLoadPurpose.Initial(), new CliContentFiles(repository.FullName), new AssetInspector());
    }

    [ClassCleanup]
    public static void DisposeContent() => _content.Dispose();

    [TestMethod]
    [DataRow(0UL)]
    [DataRow(1UL)]
    public void Standard_world_generates_with_the_configured_occupant_counts(ulong seed)
    {
        var input = Input("core:standard-world", seed);
        using var candidate = Prepare(input);
        var nodes = candidate.View.World.Nodes;
        Assert.AreEqual((int)Math.Floor(nodes.Length * input.Configuration.SettlementRatio),
            nodes.Count(node => node.Occupant is NodeOccupant.Settlement));
        Assert.AreEqual((int)Math.Floor(nodes.Length * input.Configuration.LairRatio),
            nodes.Count(node => node.Occupant is NodeOccupant.Lair));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    [DataRow("core:standard-world", 4UL, "E2F95A073E91B44257009BF9A658FAEEA28529A997B0719512C3C85142400F20")]
    [DataRow("core:standard-world", 13UL, "8EDDEB95E014FE511D25E2153AF9A031DAB452E409680DC5EAD2E6CEF154419D")]
    [DataRow("core:standard-world", 44UL, "2F945BFFA5F7F6BB45091B96F305AE770683FB1DB1BF6F99810CBFB1A46704B1")]
    [DataRow("core:basic-through-world", 0UL, "E4EEB43819BD38DE847D1BAF350AC567335C1DC50A74E7834F6E739A5AEB66BA")]
    [DataRow("core:basic-through-world", 1UL, "F8DFA0096F1F43D310D5D5DE23424B925551FAEF80963DF94B90388F7B21605E")]
    [DataRow("core:basic-through-world", 2UL, "FA052E50BE6F591F2C3A2B7B7A2226D223C823975A5C8953B6E1153BA6E0408C")]
    [DataRow("core:basic-through-world", 3UL, "6507090B35A4CFB06292AA51BEAD4811027B332D7B87F8491E1FE915170DD083")]
    [DataRow("core:basic-through-world", 4UL, "8240F5C2E6CB3BCDDBDF1177606E5A5836EF7B7427AB1E6CF71466166158E998")]
    [DataRow("core:basic-through-world", 5UL, "3946E7C0A0B18AAAC87266EB0A824E5DB5A5E26B16462FAFD0C4C7D1CE82A71B")]
    [DataRow("core:basic-through-world", 6UL, "D9B021F732A73E265EB5B233E5787F88B21002491ADE4A5252CC029CD88B5CB3")]
    [DataRow("core:basic-through-world", 7UL, "07A1159B7E340182DE8F422E269E805339A7094F903CD19CA6369304081D47A2")]
    [DataRow("core:basic-through-world", 8UL, "AED052172ACFCB5E8411B5B36AF879E9EFD4DDD90157B33031CD574BA7BCC27B")]
    public void Previously_successful_worlds_keep_their_initial_checkpoint(string profile, ulong seed, string sha256)
    {
        using var candidate = Prepare(Input(profile, seed));
        using var session = candidate.Activate();
        var result = session.Capture(CapturePurpose.Save);
        Assert.IsInstanceOfType<CoreResult<ICoreCapture>.Success>(result);
        var capture = ((CoreResult<ICoreCapture>.Success)result).Value;
        Assert.AreEqual(new SimulationPosition(0, 0), capture.Position);
        Assert.AreEqual(sha256, capture.Checkpoint.Sha256);
    }

    private static WorldGenerationInput Input(string profile, ulong seed)
    {
        var result = ContentFactory.Create().CreateWorldInput(_content.View, new(profile), seed, "stage1-world-v1");
        Assert.IsInstanceOfType<ContentResult<WorldGenerationInput>.Success>(result);
        return ((ContentResult<WorldGenerationInput>.Success)result).Value;
    }

    private static ICoreSessionCandidate Prepare(WorldGenerationInput input)
    {
        var result = CoreFactory.Create().PrepareNew(input, _content.View.Core,
            new(new("fixture", "1", "Windows", "x64", "10"), false, 1));
        var problem = result is CoreResult<ICoreSessionCandidate>.Failure failure
            ? string.Join("; ", failure.Problems.Select(p => $"{p.Code}: {p.Field}")) : result.ToString();
        Assert.IsInstanceOfType<CoreResult<ICoreSessionCandidate>.Success>(result, problem);
        return ((CoreResult<ICoreSessionCandidate>.Success)result).Value;
    }
}
