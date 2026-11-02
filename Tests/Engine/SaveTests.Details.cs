using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Godot;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class SaveTests
{
    [Test]
    public async Task Same_selected_file_reuses_pending_and_ready_details_but_busy_selection_is_read_afterwards()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var setup = new SaveSetup();
        var owned = new HashSet<Guid>();
        Train.App.App? app = null;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var writerEntered = new ManualResetEventSlim();
        using var writerRelease = new ManualResetEventSlim();
        try
        {
            app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = setup.Create;
            await fixture.AddToRoot(app);
            await _input.WaitUntil(() => app.Saves!.Status is not null);
            var saves = app.Saves!;
            owned.Add(saves.Status!.Identity.Id);
            saves.SaveAs("待查存档甲");
            await _input.WaitUntil(() => !saves.HasPendingWork);
            owned.Add(saves.Status!.Identity.Id);
            saves.SaveAs("待查存档乙");
            await _input.WaitUntil(() => !saves.HasPendingWork);
            owned.Add(saves.Status!.Identity.Id);
            var prepare = setup.Core.PrepareRestoreCall!;
            var calls = 0;
            setup.Core.PrepareRestoreCall = (capture, projection, options) =>
            {
                Interlocked.Increment(ref calls);
                entered.Set();
                Require(release.Wait(TimeSpan.FromSeconds(30)), "挂起保护：未释放详情准备。");
                return prepare(capture, projection, options);
            };
            var ui = app.GetNode<GameUi>("GameUi");
            await _input.ChooseMenu(Find<MenuButton>(ui, "GameMenu"), "存档与读档");
            var panel = ui.GetNode<SavePanel>("Root/SavePanel");
            var search = Find<LineEdit>(panel, "Search");
            await _input.WaitUntil(() => entered.IsSet);
            await _input.Enter(search, "待查存档乙");
            saves.SaveAs("");
            release.Set();
            var pendingBarrier = app.Host!.BackgroundWork.Enqueue(() => true);
            await _input.WaitUntil(() => pendingBarrier.IsCompleted && saves.Details is not null);
            var pendingCalls = Volatile.Read(ref calls);
            var ready = saves.Details;
            await _input.Enter(search, "待查存档");
            saves.SaveAs("");
            var readyBarrier = app.Host.BackgroundWork.Enqueue(() => true);
            await _input.WaitUntil(() => readyBarrier.IsCompleted && saves.Details is not null);
            var reusedReady = ReferenceEquals(ready, saves.Details) && Volatile.Read(ref calls) == pendingCalls;
            _ = app.Host.BackgroundWork.Enqueue(() =>
            {
                writerEntered.Set();
                Require(writerRelease.Wait(TimeSpan.FromSeconds(30)), "挂起保护：未释放保存队列。");
                return true;
            });
            await _input.WaitUntil(() => writerEntered.IsSet);
            saves.Save();
            await _input.Enter(search, "待查存档甲");
            var clearedBusySelection = saves.Details is null && saves.DetailsMessage is null;
            var beforeBusyRead = Volatile.Read(ref calls);
            writerRelease.Set();
            await _input.WaitUntil(() => !saves.HasPendingWork && saves.Details is not null);
            Require(pendingCalls == 1 && reusedReady && clearedBusySelection &&
                Volatile.Read(ref calls) == beforeBusyRead + 1,
                $"同一文件不可重复准备详情，忙时改选须清空旧详情并在结束后读取：pending={pendingCalls}, ready={reusedReady}, cleared={clearedBusySelection}, afterBusy={calls - beforeBusyRead}。");
        }
        finally
        {
            release.Set(); writerRelease.Set();
            if (app?.Host is { } host)
            {
                var barrier = host.BackgroundWork.Enqueue(() => true);
                await _input.WaitUntil(() => barrier.IsCompleted);
            }
            await Cleanup(fixture, app, owned);
        }
    }
}
