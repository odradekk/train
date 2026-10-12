using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App;
using Train.App.Logging;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;

namespace Train.Godot.Tests.Host;

internal sealed class HostFixture : IDisposable
{
    internal static CoreSessionOptions Options { get; } = new(new("fixture-build", "fixture-simulation",
        "fixture-os", "fixture-arch", "fixture-runtime"), true, 1);
    internal HostCoordinator Host { get; }
    internal ScriptedCoreFactory Core { get; }
    internal ScriptedContentFactory Factory { get; }
    internal ScriptedCoreSession Session { get; }
    internal ScriptedCoreCandidate CoreCandidate { get; }
    internal ScriptedContentCandidate ContentCandidate { get; }
    internal FixedContentView Content { get; }
    internal List<LogEvent> Logs { get; } = [];
    internal Func<ContentLoadRequest, ContentResult<IContentCandidate>>? Load { get; set; }
    internal Func<IContentView, GenerationProfileId, ulong, string, ContentResult<WorldGenerationInput>>? World { get; set; }
    internal Func<IContentView, System.Collections.Immutable.ImmutableArray<OwnedSettlementTemplateReference>, IContentView>? Copies { get; set; }

    internal HostFixture(Func<CoreEvent, NotificationAction>? notification = null)
    {
        Content = new(HostContentScenario.CreateProjection(HostCoreScenario.Input), new(1));
        Session = new(new FixedCoreReadView(new(0, 0), Content.Core.Identity));
        Session.SetCapture(CapturePurpose.Save, new CoreResult<ICoreCapture>.Success(Capture(new(0, 0))));
        CoreCandidate = new(Session, HostCoreScenario.References);
        ContentCandidate = new(Content);
        Core = new()
        {
            DecodeStateCall = payload => payload == "core-state" ? new CoreResult<ICoreCapture>.Success(Capture(new(0, 0)))
                : throw new InvalidOperationException("UNCONFIGURED: 核心载荷。"),
            PrepareRestoreCall = (saved, projection, options) =>
            {
                Assert.AreEqual(Content.Core.Identity, projection.Identity);
                Assert.AreEqual(Options, options);
                return new CoreResult<ICoreSessionCandidate>.Success(CoreCandidate);
            }
        };
        Factory = new()
        {
            DecodeCaptureCall = payload => payload == "content-state"
                ? new ContentResult<IContentCapture>.Success(new FixedContentCapture(payload))
                : throw new InvalidOperationException("UNCONFIGURED: 内容载荷。"),
            PrepareLoadCall = (request, _, _) => Load?.Invoke(request) ??
                (request.Purpose is ContentLoadPurpose.Restore ? new ContentResult<IContentCandidate>.Success(ContentCandidate)
                : throw new InvalidOperationException("UNCONFIGURED: 内容加载用途。")),
            CreateWorldInputCall = (view, profile, seed, version) => (World ?? throw new InvalidOperationException("UNCONFIGURED: 生成输入。"))(view, profile, seed, version),
            WithSettlementCopiesCall = (view, copies) => (Copies ?? throw new InvalidOperationException("UNCONFIGURED: 模板副本。"))(view, copies)
        };
        var ports = new UnusedPorts();
        Host = new(Core, Factory, ports, ports, new(new("builtin"), new("mods"), null, "fixture-game", []),
            Write, notification, () => 0);
    }

    internal void Write(LogLevel level, string category, string eventId, string message,
        IReadOnlyDictionary<string, object?>? attributes, Exception? exception)
        => Logs.Add(new(DateTimeOffset.UnixEpoch, Guid.Empty, Logs.Count + 1, level, category,
            eventId, message, attributes ?? new Dictionary<string, object?>(), exception is null ? null : LogExceptionData.From(exception)));

    internal ScriptedCoreSession ReadyHost()
    {
        var started = Host.BeginRestore("core-state", "content-state", Options);
        Assert.IsNull(started.Problem);
        PumpUntil(() => Host.GetStatus() is not null);
        return Session;
    }

    internal void PumpUntil(Func<bool> completed)
    {
        Assert.IsTrue(SpinWait.SpinUntil(() => { Host.ProcessFrame(0); return completed(); }, 900),
            "受控准备没有接入预期结果。" + string.Join(";", Logs.Select(log => log.Message)));
    }

    internal FixedCoreCapture Capture(SimulationPosition position) => new(new(position,
        "fixture canonical text", "fixture fixed sha256"), HostCoreScenario.References,
        "opaque core capture", HostCoreScenario.Input, new(1));
    public void Dispose() => Host.Dispose();

    private sealed class UnusedPorts : IContentFiles, IAssetInspector
    {
        public ContentIoResult<System.Collections.Immutable.ImmutableArray<ContentFileEntry>> Enumerate(ContentPath directory)
            => throw new InvalidOperationException("UNCONFIGURED: 文件枚举。" );
        public ContentIoResult<System.Collections.Immutable.ImmutableArray<byte>> Read(ContentPath file, long maximumBytes)
            => throw new InvalidOperationException("UNCONFIGURED: 文件读取。");
        public AssetInspection Inspect(AssetKind kind, AssetRevision revision,
            System.Collections.Immutable.ImmutableArray<byte> bytes)
            => throw new InvalidOperationException("UNCONFIGURED: 资产检查。");
    }
}
