using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App;
using Train.App.Logging;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Godot.Tests.Logging;

namespace Train.Godot.Tests.Host;

internal sealed class HostFixture : IDisposable
{
    internal static TimeSpan HangTimeout { get; } = TimeSpan.FromSeconds(30);
    internal static CoreSessionOptions Options { get; } = new(new("fixture-build", "fixture-simulation",
        "fixture-os", "fixture-arch", "fixture-runtime"), true, 1);
    internal HostCoordinator Host { get; }
    internal ScriptedCoreFactory Core { get; }
    internal ScriptedContentFactory Factory { get; }
    internal ScriptedCoreSession Session { get; }
    internal ScriptedCoreCandidate CoreCandidate { get; }
    internal ScriptedContentCandidate ContentCandidate { get; }
    internal FixedContentView Content { get; }
    private readonly LoggerSession _logging = LoggerSession.CreateMemory();
    private readonly Dictionary<string, int> _allowedHostFailures = [];
    internal TrainLogger Logger => _logging.Logger;
    internal IReadOnlyList<LogEvent> Logs => _logging.Memory.Snapshot();
    internal CoreContentProjection? RestoreProjection { get; private set; }
    internal CoreSessionOptions? RestoreOptions { get; private set; }
    internal Func<ContentLoadRequest, ContentResult<IContentCandidate>>? Load { get; set; }
    internal Func<IContentView, CoreReferenceManifest, ContentResult<IContentCapture>>? CaptureContent { get; set; }
    internal Func<IContentView, GenerationProfileId, ulong, string, ContentResult<WorldGenerationInput>>? World { get; set; }
    internal Func<IContentView, System.Collections.Immutable.ImmutableArray<OwnedSettlementTemplateReference>, IContentView>? Copies { get; set; }

    internal HostFixture(Func<CoreEvent, NotificationAction>? notification = null, Func<double>? seconds = null)
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
                RestoreProjection = projection;
                RestoreOptions = options;
                return new CoreResult<ICoreSessionCandidate>.Success(CoreCandidate);
            }
        };
        Factory = new()
        {
            CaptureCall = (view, references) => CaptureContent?.Invoke(view, references)
                ?? new ContentResult<IContentCapture>.Success(new FixedContentCapture("content-state", view.PersistentRevision)),
            DecodeCaptureCall = payload => payload == "content-state"
                ? new ContentResult<IContentCapture>.Success(new FixedContentCapture(payload))
                : throw new InvalidOperationException("UNCONFIGURED: 内容载荷。"),
            PrepareLoadCall = (request, _, _) => Load?.Invoke(request) ??
                (request.Purpose is ContentLoadPurpose.Restore ? new ContentResult<IContentCandidate>.Success(ContentCandidate)
                : throw new InvalidOperationException("UNCONFIGURED: 内容加载用途。")),
            CreateWorldInputCall = (view, profile, seed, version) => (World ?? throw new InvalidOperationException("UNCONFIGURED: 生成输入。"))(view, profile, seed, version),
            WithSettlementCopiesCall = (view, copies) => (Copies ?? throw new InvalidOperationException("UNCONFIGURED: 模板副本。"))(view, copies)
        };
        Host = new(Core, Factory, new ScriptedContentFiles(), new UnconfiguredAssetInspector(), new(new("builtin"), new("mods"), null, "fixture-game", []),
            Logger, notification, seconds ?? (() => 0));
    }

    internal void AllowHostFailure(string id, int count = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        _allowedHostFailures.Add(id, count);
    }

    internal ScriptedCoreSession ReadyHost()
    {
        var started = Host.BeginRestore("core-state", "content-state", Options);
        Assert.IsNull(started.Problem);
        PumpUntil(() => Host.GetStatus() is not null);
        Assert.AreEqual(Content.Core.Identity, RestoreProjection?.Identity);
        Assert.AreEqual(Options, RestoreOptions);
        Host.ProcessFrame(0);
        return Session;
    }

    internal static void WaitForSignal(ManualResetEventSlim signal, string description) =>
        Assert.IsTrue(signal.Wait(HangTimeout), $"挂起保护超时：{description}。");

    internal void PumpUntil(Func<bool> completed, double delta = 0)
    {
        Assert.IsTrue(SpinWait.SpinUntil(() => { Host.ProcessFrame(delta); return completed(); }, HangTimeout),
            "挂起保护超时：后台准备结果未接入预期边界。" + string.Join(";", Logs.Select(log => log.Message)));
    }

    internal FixedCoreCapture Capture(SimulationPosition position) => new(new(position,
        "fixture canonical text", "fixture fixed sha256"), HostCoreScenario.References,
        "opaque core capture", HostCoreScenario.Input, new(1));
    public void Dispose()
    {
        try { Host.Dispose(); }
        finally { _logging.Dispose(); }
    }

    // 由 TestCleanup 在主体通过后验证，清理期间的日志断言不能覆盖主体失败。
    internal void AssertNoUnexpectedFailures()
    {
        var logs = Logs;
        Assert.IsFalse(logs.Any(log => ContainsUnconfigured(log.Exception) ||
            log.Attributes.TryGetValue("diagnostic_message", out var message) &&
            message is string text && text.Contains("UNCONFIGURED", StringComparison.Ordinal)),
            "宿主吞掉了未配置的测试替身调用。");
        var failures = logs.Where(log => log.EventId.StartsWith("host_", StringComparison.Ordinal)
            && log.EventId.EndsWith("_failed", StringComparison.Ordinal))
            .GroupBy(log => log.EventId).ToDictionary(group => group.Key, group => group.Count());
        foreach (var (id, count) in failures)
            Assert.AreEqual(_allowedHostFailures.GetValueOrDefault(id), count, $"意外宿主失败日志：{id}。");
        foreach (var (id, count) in _allowedHostFailures)
            Assert.AreEqual(count, failures.GetValueOrDefault(id), $"预期宿主失败日志数量不符：{id}。");
    }

    private static bool ContainsUnconfigured(LogExceptionData? exception) => exception is not null
        && (exception.Message.Contains("UNCONFIGURED", StringComparison.Ordinal)
            || exception.InnerExceptions.Any(ContainsUnconfigured));
}
