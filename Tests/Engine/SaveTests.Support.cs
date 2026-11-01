using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Godot;
using Train.App;
using Train.App.Logging;
using Train.App.Saves;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class SaveTests
{
    private static T Dialog<T>(SavePanel panel, string title) where T : Window =>
        panel.GetChildren().OfType<T>().Single(dialog => dialog.Title == title);

    private static void Press(SavePanel panel, string text)
    {
        var button = UiTestInput.SaveButton(panel, text);
        Require(!button.Disabled && button.Visible, $"按钮不可用：{text}。");
        button.EmitSignal(BaseButton.SignalName.Pressed);
    }

    private async Task Cleanup(Fixture fixture, Train.App.App? app, HashSet<Guid> owned)
    {
        try
        {
            if (app?.Saves is { } saves)
            {
                if (saves.Status is { } status) owned.Add(status.Identity.Id);
                saves.Cancel();
                await _input.WaitUntil(() => !saves.HasPendingWork);
            }
        }
        finally
        {
            try { await fixture.Cleanup(); }
            finally
            {
                UiTestInput.DeleteSaves(owned);
            }
        }
    }

    private sealed class SaveSetup
    {
        internal ScriptedCoreSession? Session { get; private set; }
        internal ScriptedContentFactory? Content { get; private set; }
        internal bool MissingOnRestore { get; set; }

        internal HostCoordinator Create(TrainLogger logger)
        {
            var capture = new FixedCoreCapture(new(new(0, 0), "engine save", "engine sha"),
                HostCoreScenario.References, "engine core", HostCoreScenario.Input, new(0));
            var core = new ScriptedCoreFactory
            {
                DecodeStateCall = payload => payload == "engine core"
                    ? new CoreResult<ICoreCapture>.Success(capture) : throw new InvalidOperationException("UNCONFIGURED: core payload"),
                PrepareRestoreCall = (_, projection, _) =>
                {
                    Require(projection.Identity == HostCoreScenario.Input, "恢复时收到错误内容投影。");
                    Session = new(UiWorldFixture.Save(projection.Identity));
                    Session.SetCapture(CapturePurpose.Save, new CoreResult<ICoreCapture>.Success(capture));
                    Session.ReadRecordingCall = () => new(new(0, 0), [new(new(1), capture)], [], [capture.Checkpoint], [], [], capture.References);
                    Session.ConfirmRecordingStartCall = (point, _) => point.Value == 1 ? new([], [])
                        : throw new InvalidOperationException("UNCONFIGURED: recording point");
                    return new CoreResult<ICoreSessionCandidate>.Success(new ScriptedCoreCandidate(Session, HostCoreScenario.References));
                }
            };
            Content = new()
            {
                CaptureReplayCall = (history, _) => new ContentResult<IContentReplayCapture>.Success(new FixedContentReplayCapture(history, "engine recording content")),
                CaptureCall = (view, _) => new ContentResult<IContentCapture>.Success(new FixedContentCapture("engine content", view.PersistentRevision)),
                DecodeCaptureCall = payload => payload == "engine content"
                    ? new ContentResult<IContentCapture>.Success(new FixedContentCapture(payload)) : throw new InvalidOperationException("UNCONFIGURED: content payload"),
                PrepareLoadCall = (request, _, _) =>
                {
                    Require(request.Purpose is ContentLoadPurpose.Restore, "存档引擎情景只允许恢复。");
                    var view = new FixedContentView(HostContentScenario.CreateProjection(HostCoreScenario.Input), new(1))
                    {
                        Designs = new Dictionary<DesignHash, ContentLookup<DesignEvaluation>>
                        { [new("engine-vehicle")] = new ContentLookup<DesignEvaluation>.NotFound() }
                    };
                    var report = new ContentPreparationReport([], MissingOnRestore
                        ? [new(new ContentReference.Generation(new("fixture:missing")), null, "runtime", [])] : [], [], []);
                    return new ContentResult<IContentCandidate>.Success(new ScriptedContentCandidate(view, report: report));
                }
            };
            var options = new CoreSessionOptions(new("fixture", "fixture", "fixture", "fixture", "fixture"), true, 1);
            var host = new HostCoordinator(core, Content, new ScriptedContentFiles(), new UnconfiguredAssetInspector(),
                new(new("builtin"), new("mods"), null, "engine-save", []), logger, sessionOptions: options);
            Require(host.BeginRestore("engine core", "engine content", options).Problem is null, "无法开始初始恢复。");
            return host;
        }

    }
}
