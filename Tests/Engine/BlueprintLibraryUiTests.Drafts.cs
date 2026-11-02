using System.Collections.Immutable;
using Chickensoft.GoDotTest;
using Godot;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class BlueprintLibraryUiTests
{
    [Test]
    public async Task Escape_save_failure_keeps_the_session_and_successful_retry_returns_to_the_library()
    {
        var fail = true;
        var fixture = await Ready(configure: factory => factory.PrepareEditCall = (basis, edit) =>
        {
            if (fail) return new ContentResult<IContentCandidate>.Faulted("fixture_save_failure", "technical failure", null);
            var saved = ((ContentEdit.SaveLocalDraft)edit).Draft;
            return new ContentResult<IContentCandidate>.Success(new ScriptedContentCandidate(
                new FixedContentView(basis.Core, basis.Generation, new(1)) { Blueprints = [], LocalDrafts = [saved], Voxels = [], GenerationProfiles = [] },
                basis, ContentChange.Persistent, edit: new PreparedContentEdit.DraftSaved(saved.Id, saved.Revision)));
        });
        try
        {
            var library = Library(fixture); var input = new UiTestInput(TestScene);
            await input.Activate(Button(library, "编辑", exact: true));
            await FramesUntil(TestScene, () => fixture.Editor.Session is not null);
            var session = fixture.Factory.Session!; session.SetState(state => state with { IsDirty = true });
            fixture.Editor.Process();
            var panel = fixture.App.FindChildren("*", "", true, false).OfType<DesignEditorPanel>().Single();
            await FramesUntil(TestScene, () => panel.IsVisibleInTree());
            Find<Button>(panel, "Properties").GrabFocus(); await input.PressKey(Key.Escape);
            await FramesUntil(TestScene, () => Find<Button>(panel, "SaveAndLeave").IsVisibleInTree());
            await input.Activate(Find<Button>(panel, "SaveAndLeave"));
            await FramesUntil(TestScene, () => !fixture.Editor.IsBusy && fixture.Editor.HasError);
            Require(ReferenceEquals(fixture.Editor.Session, session) && fixture.Editor.IsDirty && !library.IsVisibleInTree(),
                "保存失败后必须保留原编辑会话和修改，不能返回蓝图库。");
            fail = false;
            Find<Button>(panel, "Properties").GrabFocus(); await input.PressKey(Key.Escape);
            await FramesUntil(TestScene, () => Find<Button>(panel, "SaveAndLeave").IsVisibleInTree());
            await input.Activate(Find<Button>(panel, "SaveAndLeave"));
            await FramesUntil(TestScene, () => !fixture.Editor.IsOpen && library.IsVisibleInTree());
            Require(fixture.App.Host!.Content!.LocalDrafts.Length == 1, "成功重试必须先保存草稿，再返回蓝图库。");
        }
        finally { fixture.QueueFree(); await new UiTestInput(TestScene).Frames(3); }
    }

    [Test]
    public async Task Usage_counts_existing_owned_vehicles_once_and_ignores_other_designs_and_buildings()
    {
        var fixture = await Ready(Vehicles());
        try { Require(Shown(Library(fixture), "2 辆车使用这个设计"), "应合计编组与停放的自有同设计车辆，排除非自有、其他设计、建筑、已不存在对象和重复位置项。"); }
        finally { fixture.QueueFree(); await new UiTestInput(TestScene).Frames(3); }
    }

    [Test]
    public async Task Delete_and_discard_require_confirmation_and_cancel_preserves_the_item()
    {
        foreach (var discard in new[] { false, true })
        {
            var draft = new SavedContentDraft(new(Guid.NewGuid()), new(1), new DraftTarget.Instance(new InstanceId.Vehicle(new(1))),
                new DraftPayload.Design(EditorPreviewSession.Draft with { Display = new("待续设计", ""), Specification = new DesignSpecification.Vehicle(SizeClass.Small) }));
            var calls = 0;
            var fixture = await Ready(Vehicles(), discard ? [draft] : [], factory => factory.PrepareEditCall = (basis, edit) =>
            {
                calls++;
                PreparedContentEdit result = edit switch
                {
                    ContentEdit.DeleteLocalDraft deleted => new PreparedContentEdit.DraftDeleted(deleted.Draft),
                    ContentEdit.DeleteBlueprint deleted => new PreparedContentEdit.BlueprintDeleted(deleted.Target),
                    _ => throw new InvalidOperationException("本用例只允许删除选中的条目。")
                };
                return new ContentResult<IContentCandidate>.Success(new ScriptedContentCandidate(
                    new FixedContentView(basis.Core, basis.Generation, new(1)) { Blueprints = [], LocalDrafts = [], Voxels = [], GenerationProfiles = [] },
                    basis, ContentChange.Persistent, edit: result));
            });
            try
            {
                var library = Library(fixture); var input = new UiTestInput(TestScene);
                if (discard)
                {
                    await input.Activate(Button(library, "草稿", exact: true));
                    Require(Shown(library, "改装：列车 1 的第 1 辆车"), "改装草稿应说明列车和编组位置。");
                }
                var item = discard ? "待续设计" : "本局货车";
                await input.Activate(Button(library, discard ? "丢弃…" : "删除…", exact: true));
                var dialog = library.GetChildren().OfType<ConfirmationDialog>().Single(window => window.Visible);
                Require(calls == 0 && Cards(library).Contains(item), "弹出确认时不能提前删除。");
                await input.Activate(dialog.GetCancelButton(), dialog);
                Require(calls == 0 && Cards(library).Contains(item), "取消必须保留条目。");
                await input.Activate(Button(library, discard ? "丢弃…" : "删除…", exact: true));
                dialog = library.GetChildren().OfType<ConfirmationDialog>().Single(window => window.Visible);
                await input.Activate(dialog.GetOkButton(), dialog);
                await FramesUntil(TestScene, () => !Cards(library).Contains(item));
                Require(calls == 1, "只有确认后才能执行一次删除并刷新列表。");
            }
            finally { fixture.QueueFree(); await new UiTestInput(TestScene).Frames(3); }
        }
    }

    [Test]
    public async Task Draft_evaluation_does_not_block_library_navigation_or_use_the_ui_thread()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var draft = new SavedContentDraft(new(Guid.NewGuid()), new(1), new DraftTarget.LocalBlueprint(null), new DraftPayload.Design(EditorPreviewSession.Draft));
        var uiThread = System.Environment.CurrentManagedThreadId;
        var fixture = await Ready(drafts: [draft], configure: factory => factory.EvaluateDesignCall = (_, design) =>
        {
            entered.TrySetResult(System.Environment.CurrentManagedThreadId);
            try { release.Wait(TimeSpan.FromSeconds(5)); return new ContentResult<DesignEvaluation>.Success(EditorPreviewSession.Evaluate(design)); }
            finally { finished.TrySetResult(); }
        });
        try
        {
            await FramesUntil(TestScene, () => entered.Task.IsCompleted);
            Require(entered.Task.Result != uiThread, "草稿设计评估必须离开 Godot 主线程。");
            var library = Library(fixture);
            await new UiTestInput(TestScene).Activate(Button(library, "内容包", exact: true));
            Require(Cards(library).Contains("内容包机车") && !finished.Task.IsCompleted, "草稿评估未完成时仍必须能切组并操作蓝图库。");
            release.Set(); await FramesUntil(TestScene, () => finished.Task.IsCompleted);
        }
        finally { release.Set(); fixture.QueueFree(); await new UiTestInput(TestScene).Frames(3); }
    }

    private static FixedCoreReadView Vehicles(string otherDesign = "other-design")
    {
        var basis = UiWorldFixture.Map();
        var node = basis.World.Nodes[0];
        var edge = new EdgeMapItem(new(1), node.Id, basis.World.Nodes[1].Id, 500, node.Terrain, 1, 1, [], []);
        WorldLocation first = new WorldLocation.Node(node.Id), second = new WorldLocation.Edge(edge.Id, 0);
        InstanceView Vehicle(ulong number, SettlementRelation relation, string hash) => new(new InstanceId.Vehicle(new(number)), new(hash), new("composition"), first,
            relation, new InstanceDetails.Vehicle(SizeClass.Small, number == 1 ? new TrainId(1) : null), InstanceLock.None, new(1, 1), [], []);
        var instances = new[] { Vehicle(1, SettlementRelation.PlayerOwned, "local-wagon"), Vehicle(2, SettlementRelation.NonHostile, "local-wagon"),
            Vehicle(3, SettlementRelation.PlayerOwned, otherDesign), Vehicle(5, SettlementRelation.PlayerOwned, "local-wagon"),
            new InstanceView(new InstanceId.Building(new(4)), new("local-wagon"), new("composition"), first, SettlementRelation.PlayerOwned,
                new InstanceDetails.Building(new(1), PlotSize.Small), InstanceLock.None, new(1, 1), [], []) };
        var train = ((Lookup<TrainView>.Found)basis.FindTrain(new(7))).Value with { Id = new(1), Vehicles = [new(1)] };
        return new(default, HostCoreScenario.Input)
        {
            Economy = UiWorldFixture.Economy,
            World = basis.World with { Nodes = [node], Edges = [edge], Trains = [] },
            Locations = new Dictionary<WorldLocation, Lookup<LocationView>>
            {
                [first] = new Lookup<LocationView>.Found(new(first, [], [.. instances.Take(3).Select(instance => instance.Id), instances[4].Id, new InstanceId.Vehicle(new(6))], [], [], null)),
                [second] = new Lookup<LocationView>.Found(new(second, [], [instances[0].Id, instances[3].Id], [], [], null))
            },
            Instances = instances.ToDictionary(instance => instance.Id, instance => (Lookup<InstanceView>)new Lookup<InstanceView>.Found(instance))
                .Append(new KeyValuePair<InstanceId, Lookup<InstanceView>>(new InstanceId.Vehicle(new(6)), new Lookup<InstanceView>.Missing())).ToDictionary(),
            Trains = new Dictionary<TrainId, Lookup<TrainView>> { [new(1)] = new Lookup<TrainView>.Found(train) }
        };
    }
}
