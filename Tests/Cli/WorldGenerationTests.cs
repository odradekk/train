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
    [DataRow("core:standard-world", 4UL, "0B0B81117DF215C56A673C264458C3855525238BDFE54B71D93791EE2C675E0D")]
    [DataRow("core:standard-world", 13UL, "0BC18A73378F2C80D246D64382B0A29B5AC84E355FC96D5E360BB700EDB7B699")]
    [DataRow("core:standard-world", 44UL, "DE980A72137D3145284CFABDAC976BB8EAD6F40DD0E77CFFBF39AA900AD78065")]
    [DataRow("core:basic-through-world", 0UL, "4424FCEF928EFC5A84C74E777346B9CE5CC0A2103CF6474FD4496F4BBFEEAB59")]
    [DataRow("core:basic-through-world", 1UL, "05632C61F71AD99A974EBD6DB2C5407B921709F90C8DBD4320C3E8CB33A28A39")]
    [DataRow("core:basic-through-world", 2UL, "26DFD97651B4D9A46683E474BC78D9669801ECEA3C18B2918FF0B68D9D97977C")]
    [DataRow("core:basic-through-world", 3UL, "F990817A7CA957A39CE90BA9F10DBA26BF4EA88EECF59FC223080ECE5B107ADB")]
    [DataRow("core:basic-through-world", 4UL, "99238A6562FC8FE8395FC80897423CE6DF41B688EBAAC3F268D89D87CF8D1CEB")]
    [DataRow("core:basic-through-world", 5UL, "78A40B3B4D5ABC1038B07883B9A72D5F47E042FAD22FA96E0EB5A0B5ED2D6226")]
    [DataRow("core:basic-through-world", 6UL, "C64121A718F657273BA40724A948863DECFB0C243A90772201BA691783FBA08B")]
    [DataRow("core:basic-through-world", 7UL, "CB77DBD9E21B12294DDED7A6C19759A364E417BE6C4855249639C13B7116F8E9")]
    [DataRow("core:basic-through-world", 8UL, "83BE89A1480F2B00E895A4F26FE251177A7977F1E69F816D8EDB270BFB0A206F")]
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
