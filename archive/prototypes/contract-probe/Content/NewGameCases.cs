// 独立新局候选的固定情景：仅检查公开入口顺序、归属与释放。
// 生成配置是可构造输入，不执行世界生成，也不声称满足完整 Demo 配平。
using System.Collections.Immutable;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Examples = Train.Content.ContractSamples.ConsumerExamples;
using static Train.ContractProbe.Content.Program;

namespace Train.ContractProbe.Content;

internal static class NewGameCases
{
    internal static readonly CoreReferenceManifest References = new([], [new(new(7), new("core:template"), [new(1, PlotSize.Small)])], [], [Input]);
    internal static readonly ContentInstallation Installation = new(new("core"), new("mods"), null, "probe", []);
    private static readonly RuntimeIdentity Runtime = new("probe", "probe", "Windows", "x64", ".NET10");

    internal static void Run(IContentView basis, IContentView paired, bool failCopies)
    {
        using var session = new CoreSession(_ => throw Unsupported.Call("Submit"));
        var installed = new Candidate(null, basis, ContentChange.CoreInput);
        var core = new CoreCandidate(session);
        var worldInput = WorldInput();
        var copied = false;
        var contentFactory = new Factory
        {
            Load = request => ReferenceEquals(request.Installation, Installation) && request.Purpose is ContentLoadPurpose.Initial
                ? new ContentResult<IContentCandidate>.Success(installed) : throw Unsupported.Call("PrepareLoad"),
            World = received =>
            {
                Check(ReferenceEquals(received, basis), "generation_not_fixed_view");
                return new ContentResult<WorldGenerationInput>.Success(worldInput);
            },
            Copies = (received, references) =>
            {
                Check(ReferenceEquals(received, basis) && references.SequenceEqual(References.Templates), "new_game_wrong_copies");
                Check(core.Activations == 0 && installed.Activations == 0, "new_game_activated_before_copies");
                if (failCopies) throw new InvalidOperationException("copy_failed");
                copied = true;
                return paired;
            }
        };
        var options = new CoreSessionOptions(Runtime, true, 2);
        var factory = new CoreFactory(core, basis.Core, worldInput, options);
        if (failCopies)
        {
            Expect("copy_failed", () => Examples.PrepareNew(contentFactory, factory, Installation, new NoFiles(), new NoAssets(), new("core:profile"), 1, "probe", options, _ => { }));
            Check(core.Disposed && installed.Disposed && core.Activations == 0 && installed.Activations == 0, "failed_new_game_leaked");
            return;
        }
        var candidates = Examples.PrepareNew(contentFactory, factory, Installation, new NoFiles(), new NoAssets(), new("core:profile"), 1, "probe", options, _ => { });
        Check(candidates is not null && copied && core.Activations == 0, "new_game_preparation");
        var active = Examples.ActivateNew(candidates!);
        Check(ReferenceEquals(active.Core, session) && ReferenceEquals(active.Content, paired), "new_game_incomplete_pair");
        Check(core.Activations == 1 && installed.Activations == 1 && core.Disposed && installed.Disposed, "new_game_lifetime");
    }

    private static WorldGenerationInput WorldInput() => new(1, "probe", new(new string('1', 64)),
        new(1000, 1000, 10, 1, [new(100, 1)], [new(100, 1)], [new(100, 1)], 0, 1,
            [new(new("core:plain"), 0, 1, 0, 1, 1, 1, 1)], 10, 0.1, 0, 0, [], [],
            new(1, 1, 1, 0), new(1, 1, 1000, 1000), new(true, 0, 0, 0, []),
            new(1, 1, true, true, 1, new(new string('2', 64))),
            new(new("core:population"), new("core:market"), new("core:construction"), new("core:railway"), new("core:combat"))), new(new(100), []));
}

internal sealed class CoreCandidate(ICoreSession session) : ICoreSessionCandidate
{
    public int Activations { get; private set; }
    public bool Disposed { get; private set; }
    public ICoreReadView View => throw Unsupported.Call(nameof(View));
    public CoreReferenceManifest References => NewGameCases.References;
    public ImmutableArray<CoreProblem> AdaptationReport => [];
    public ImmutableArray<MissingCoreReference> MissingReferences => [];
    public ICoreSession Activate()
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        if (Activations++ != 0) throw new InvalidOperationException("already_activated");
        return session;
    }
    public void Dispose() => Disposed = true;
}

internal sealed class CoreFactory(CoreCandidate candidate, CoreContentProjection content, WorldGenerationInput world, CoreSessionOptions expectedOptions) : ICoreFactory
{
    public CoreResult<ICoreSessionCandidate> PrepareNew(WorldGenerationInput input, CoreContentProjection received, CoreSessionOptions options)
    {
        Program.Check(ReferenceEquals(content, received), "new_game_content_changed");
        if (!ReferenceEquals(input, world) || options != expectedOptions) throw Unsupported.Call(nameof(PrepareNew));
        return new CoreResult<ICoreSessionCandidate>.Success(candidate);
    }
    public CoreResult<ICoreCapture> DecodeState(string payload) => throw Unsupported.Call(nameof(DecodeState));
    public CoreResult<ICoreSessionCandidate> PrepareRestore(ICoreCapture capture, CoreContentProjection received, CoreSessionOptions options) => throw Unsupported.Call(nameof(PrepareRestore));
    public CoreResult<ICoreReplay> PrepareReplay(CoreReplayInput input, RuntimeIdentity environment, ImmutableArray<CoreContentProjection> history) => throw Unsupported.Call(nameof(PrepareReplay));
    public CoreResult<CheckpointComparison> CompareCheckpoints(CoreCheckpoint expected, CoreCheckpoint actual) => throw Unsupported.Call(nameof(CompareCheckpoints));
}

internal sealed class ContentCapture : IContentCapture
{
    public ContentRevision Revision => new(1);
    public ImmutableArray<DesignHash> Designs => [];
    public ImmutableArray<OwnedSettlementTemplateReference> Templates => NewGameCases.References.Templates;
    public ImmutableArray<PackageMetadata> ReferencedPackages => [];
    public string Encode() => throw Unsupported.Call(nameof(Encode));
}

internal sealed class CoreCapture : ICoreCapture
{
    public SimulationPosition Position => new(0, 0);
    public CoreRevision Revision => new(0);
    public CoreInputId InputIdentity => Program.Input;
    public CoreReferenceManifest References => NewGameCases.References;
    public CoreCheckpoint Checkpoint => throw Unsupported.Call(nameof(Checkpoint));
    public string Encode() => throw Unsupported.Call(nameof(Encode));
}
