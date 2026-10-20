using Chickensoft.GoDotTest;
using Godot;
using Train.App;
using Train.App.Assets;
using Train.App.Logging;
using Train.App.Recording;
using Train.App.Saves;
using Train.Content.Contracts;
using Train.Core.Contracts;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public class VehicleServiceIntegrationTests(Node testScene) : TestClass(testScene)
{
    [Test]
    public async Task Local_design_commission_survives_files_and_replays_rejected_and_accepted_targets()
        => await RunScenario(true);

    [Test]
    public async Task Saved_instance_target_refit_survives_files_and_strict_replay()
        => await RunScenario(false);

    private async Task RunScenario(bool manufacture)
    {
        var directory = ContentFixture.CopyBuiltIn();
        try
        {
            VehicleServiceDeconstructionIntegrationTests.PrepareServiceWorldContent(directory);
            using var dispatcher = new GodotMainThreadDispatcher();
            using var logger = new TrainLogger(Path.Combine(directory, "test-logs"), LogLevel.Info, new GodotLogOutput());
            using var host = ContentFixture.CreateHost(logger, directory, dispatcher);
            using var restored = ContentFixture.CreateHost(logger, directory, dispatcher);
            async Task Pump(HostCoordinator current, TimeSpan? timeout = null) => await FramesUntil(TestScene, () =>
            {
                current.ProcessFrame(0);
                return !current.IsBusy;
            }, dispatcher, timeout);

            Require(host.BeginNewGame(new("core:basic-through-world"), 1, "stage1-world-v1", host.SessionOptions!).Problem is null,
                "真实制造情景未能开始新局。");
            await Pump(host, ContentPreparationTimeout);
            Require(host.GetStatus() is not null, "真实制造情景没有活动会话。");

            var original = ((ContentLookup<BlueprintRecord>.Found)host.Content!.FindBlueprint(
                new(BlueprintLibrary.Packages, new("core:small-freight-wagon")))).Value.Definition;
            var draft = original with
            {
                Display = new("真实服务制造测试", "本局新蓝图"),
                Layout = new(
                    [new(new("core:bogie"), VoxelOrientation.PxPy, null, null),
                     new(new("core:structural-metal"), VoxelOrientation.PxPy, null, null)],
                    [new(new(0, 0, 0), 0), new(new(0, 1, 0), 1)], [])
            };
            if (!manufacture) draft = draft with { Display = new("真实实例正式改装", "移动结构金属"),
                Layout = draft.Layout with { Cells = [new(new(0, 0, 0), 0), new(new(1, 0, 0), 1)] } };
            Require(host.BeginContentEdit(new ContentEdit.SaveBlueprint(new BlueprintSaveTarget.New(BlueprintLibrary.Local), draft)).Problem is null,
                "本局蓝图保存请求被拒绝。");
            await Pump(host);
            var saved = host.Content!.Blueprints.Single(item => item.Address.Library == BlueprintLibrary.Local && item.Display.Name == draft.Display.Name);
            var savedDraft = ((ContentLookup<BlueprintRecord>.Found)host.Content.FindBlueprint(saved.Address)).Value.Definition;

            var store = new FileSaveStore(directory);
            async Task RestoreAndCompare(string name)
            {
                var capture = Saved(host);
                var path = Path.Combine(directory, name + ".save.json");
                Require(store.Write(path, capture.Encode(capture.Identity, DateTimeOffset.UtcNow), false).Published, "真实存档未写入：" + path);
                var document = store.Read(path);
                Require(restored.BeginRestore(document.CorePayload, document.ContentPayload, host.SessionOptions!, capture.Identity).Problem is null,
                    "真实文件恢复请求被拒绝。");
                await Pump(restored, ContentPreparationTimeout);
                Require(restored.GetStatus() is not null && Saved(restored).Core.Encode() == capture.Core.Encode(),
                    "真实保存恢复改变核心状态，或重复扣款、扣料、创建任务。");
                Require(restored.Content!.FindBlueprint(saved.Address) is ContentLookup<BlueprintRecord>.Found,
                    "真实恢复丢失本局新蓝图。");
            }
            if (manufacture)
            {
                using var target = Success(host.ContentFactory.PrepareEdit(host.Content, new ContentEdit.ConstructionTarget(savedDraft)));
                var design = ((PreparedContentEdit.ConstructionTarget)target.Edit!).Design;
                var view = host.GetStatus()!.View;
                var offers = view.World.Nodes.Select(item => item.Occupant).OfType<NodeOccupant.Settlement>()
                    .Select(item => Found(view.FindSettlement(item.Id)))
                    .Where(item => item.Relation == SettlementRelation.NonHostile)
                    .SelectMany(settlement => settlement.Plots.Select(plot => plot.Occupant).OfType<PlotOccupant.Building>()
                        .Select(building => (Settlement: settlement, Request: new JobRequest.BuildVehicle(
                            new(building.Id, CapabilityKind.Construction), design, [], 0))))
                    .Select(item => (item.Settlement, item.Request, Quote: Quote(view, item.Request)))
                    .Where(item => item.Quote.Blockers.IsEmpty && item.Quote.TotalPayment is { Value: > 0 }).ToArray();
                Require(offers.Length > 0, "正式新局没有可负担的小型车辆委托：设施、库存或资金不足。");
                var offer = offers[0];
                var serviceSettlement = offer.Settlement;
                var before = EconomicState(view, serviceSettlement.Id);
                var content = host.Content;
                var rejectedDraft = savedDraft with { Display = new("仅拒绝命令引用的设计", "不得激活") };
                using (var rejectedTarget = Success(host.ContentFactory.PrepareEdit(content, new ContentEdit.ConstructionTarget(rejectedDraft))))
                {
                    var rejectedDesign = ((PreparedContentEdit.ConstructionTarget)rejectedTarget.Edit!).Design;
                    var rejected = host.Submit(host.GetStatus()!.Binding,
                        new CoreCommand.CommissionVehicleWork(offer.Request with { Design = rejectedDesign }, new(offer.Quote.TotalPayment!.Value.Value - 1)),
                        CommandSource.AutomationTest, rejectedTarget);
                    Require(rejected is SubmissionResult.Completed { Receipt.Decision: CommandDecision.Rejected rejectedDecision }
                        && rejectedDecision.Problems.Any(p => p.Code == "price_condition_failed"), "低于报价的委托未按价格条件拒绝：" + rejected);
                    Require(ReferenceEquals(content, host.Content) && before == EconomicState(host.GetStatus()!.View, serviceSettlement.Id),
                        "拒绝委托激活了目标或改变资金、库存、任务。");
                }

                using var acceptedTarget = Success(host.ContentFactory.PrepareEdit(host.Content!, new ContentEdit.ConstructionTarget(savedDraft)));
                var acceptedDesign = ((PreparedContentEdit.ConstructionTarget)acceptedTarget.Edit!).Design;
                var request = offer.Request with { Design = acceptedDesign };
                view = host.GetStatus()!.View;
                var quote = Quote(view, request);
                var wallet = view.Economy.Wallet.Value;
                var inventory = Found(view.FindInventory(serviceSettlement.Inventory));
                var submitted = host.Submit(host.GetStatus()!.Binding, new CoreCommand.CommissionVehicleWork(request, quote.TotalPayment!.Value),
                    CommandSource.AutomationTest, acceptedTarget);
                Require(submitted is SubmissionResult.Completed { Receipt.Decision: CommandDecision.Accepted }, "真实委托未被接受：" + submitted);
                Require(ReferenceEquals(acceptedTarget.View, host.Content), "接受委托没有使用准备好的目标内容。");
                var completed = (SubmissionResult.Completed)submitted;
                var job = completed.Events.Select(e => e.Event).OfType<CoreEvent.ConstructionStarted>().Single().Job.Id;
                view = host.GetStatus()!.View;
                Require(view.Economy.Wallet.Value == wallet - quote.TotalPayment.Value.Value, "委托扣款与核心报价不同。");
                var paidInventory = Found(view.FindInventory(serviceSettlement.Inventory));
                Require(!quote.NpcPurchases.IsEmpty, "空来源委托没有覆盖真实 NPC 补购。");
                foreach (var purchase in quote.NpcPurchases)
                    Require(Quantity(paidInventory, purchase.Good) == Quantity(inventory, purchase.Good) - purchase.Quantity,
                        "NPC 补购扣料与核心报价不同：" + purchase.Good);
                Require(Found(view.FindJob(job)) is JobView.Construction, "接受委托后没有真实施工任务。");

                await RestoreAndCompare("working");

                VehicleId? vehicle = null;
                host.BoundaryCompleted += (_, _, _, events) =>
                {
                    foreach (var result in events.Select(e => e.Event).OfType<CoreEvent.ConstructionCompleted>().Where(e => e.Job == job))
                        if (result.CreatedInstance is InstanceId.Vehicle created) vehicle = created.Id;
                };
                // 两件货物共 16 工作量；正式车间每秒 4 工作量，600 步留出资源分配余量。
                for (var step = 0; step < 600 && vehicle is null; step++)
                {
                    Require(host.Step(1).Completed == 1, "真实委托施工无法推进。");
                    if (step % 50 == 49)
                        await TestScene.ToSignal(TestScene.GetTree(), SceneTree.SignalName.ProcessFrame);
                }
                var unfinished = host.GetStatus()!.View.FindJob(job);
                var jobDetails = unfinished is Lookup<JobView>.Found { Value: JobView.Construction construction }
                    ? $"状态 {construction.Value.State}，工作量 {construction.Value.ProgressWork}/{construction.Value.FrozenTotalWork}，阻止原因 {string.Join(';', construction.Value.Blockers.Select(p => p.Code))}"
                    : unfinished.ToString();
                Require(vehicle is not null, "真实委托未在 600 步内发出新车辆完成事件：" + jobDetails);
                var formed = host.Submit(host.GetStatus()!.Binding, new CoreCommand.FormTrain([vehicle!.Value]), CommandSource.AutomationTest);
                Require(formed is SubmissionResult.Completed { Receipt.Decision: CommandDecision.Accepted }, "新制造车辆无法真实编组：" + formed);
                var instance = Found(host.GetStatus()!.View.FindInstance(new InstanceId.Vehicle(vehicle.Value)));
                Require(instance.Details is InstanceDetails.Vehicle { Train: not null } && instance.Design == acceptedDesign.Design.Design,
                    "新车设计或编组查询不一致。");
            }
            else
            {
                var startingTrain = Found(host.GetStatus()!.View.FindTrain(host.GetStatus()!.View.World.Trains.Single().Id));
                var smallDesign = host.Content!.Blueprints.Single(b => b.Display.Name == "拆解最小车辆").Design;
                var instance = startingTrain.Vehicles.Select(id => Found(host.GetStatus()!.View.FindInstance(new InstanceId.Vehicle(id)))).Single(i => i.Design == smallDesign);
                var node = ((TrainPosition.Node)startingTrain.Position).Id;
                var service = host.GetStatus()!.View.World.Nodes.Single(n => n.Id == node).Occupant as NodeOccupant.Settlement;
                var serviceSettlement = Found(host.GetStatus()!.View.FindSettlement(service!.Id));
                var facility = serviceSettlement.Plots.Select(p => p.Occupant).OfType<PlotOccupant.Building>()
                    .Select(p => new FacilityKey(p.Id, CapabilityKind.Construction))
                    .First(key => host.GetStatus()!.View.FindInstance(new InstanceId.Building(key.Building)) is Lookup<InstanceView>.Found building &&
                        host.Content.FindDesign(building.Value.Design) is ContentLookup<DesignEvaluation>.Found
                            { Value: DesignEvaluation.Complete { Core: { } input } } &&
                        input.Design.Parts.Any(part => part.Capabilities.Any(capability => capability is PartCapability.Construction)));
                var vehicleInstance = instance.Id;
                var startingBlueprint = host.Content!.Blueprints.Single(b => b.Display.Name == "拆解最小车辆");
                var startingDefinition = ((ContentLookup<BlueprintRecord>.Found)host.Content.FindBlueprint(startingBlueprint.Address)).Value.Definition;
                var renamed = startingDefinition with { Display = new("真实实例立即应用", "只改变显示资料") };
                using (var immediate = Success(host.ContentFactory.PrepareEdit(host.Content!, new ContentEdit.InstanceTarget(instance.Design, renamed))))
                {
                    var difference = ((PreparedContentEdit.InstanceTarget)immediate.Edit!).Difference;
                    Require(difference.CanApplyImmediately, "仅显示资料变化未被内容判为可立即应用。");
                    var money = host.GetStatus()!.View.Economy.Wallet;
                    var applied = host.Submit(host.GetStatus()!.Binding, new CoreCommand.ApplyInstanceDesign(vehicleInstance, difference.Core),
                        CommandSource.AutomationTest, immediate);
                    Require(applied is SubmissionResult.Completed { Receipt.Decision: CommandDecision.Accepted }
                        && ReferenceEquals(host.Content, immediate.View), "真实立即应用未原子交接实例目标：" + applied);
                    instance = Found(host.GetStatus()!.View.FindInstance(vehicleInstance));
                    Require(instance.Design == difference.Core.Target.Design.Design && host.GetStatus()!.View.Economy.Wallet == money,
                        "立即应用没有更新实例设计，或意外扣款。");
                }

                var refitBlueprint = saved;
                var refitDefinition = savedDraft;
                var leaseDuration = (ulong)host.Content!.Core.MarketRules.Single().LeaseDurationTicks;
                var leaseRequest = new LeaseRequest.Rent(serviceSettlement.Id, 10, leaseDuration);
                var leaseQuote = host.GetStatus()!.View.QuoteLease(leaseRequest) as CoreResult<LeaseQuote>.Success;
                Require(leaseQuote is { Value.Blockers.IsEmpty: true, Value.Total: not null }, "当地真实租库没有有效报价。");
                var leaseResult = host.Submit(host.GetStatus()!.Binding, new CoreCommand.AcquireLease(leaseRequest, leaseQuote!.Value.Total!.Value), CommandSource.AutomationTest);
                Require(leaseResult is SubmissionResult.Completed { Receipt.Decision: CommandDecision.Accepted }, "真实租库失败：" + leaseResult);
                var lease = Found(host.GetStatus()!.View.FindLease(Found(host.GetStatus()!.View.FindSettlement(serviceSettlement.Id)).Leases.Single()));
                var returnInventory = Found(host.GetStatus()!.View.FindInventory(lease.Inventory));
                using var refitTarget = Success(host.ContentFactory.PrepareEdit(host.Content, new ContentEdit.InstanceTarget(instance.Design, refitDefinition)));
                var refitDifference = ((PreparedContentEdit.InstanceTarget)refitTarget.Edit!).Difference;
                Require(!refitDifference.CanApplyImmediately, "移动结构金属被错误判为立即应用。");
                var refitRequest = new JobRequest.Refit(facility, vehicleInstance, refitDifference.Core, [], 0);
                var refitQuote = Quote(host.GetStatus()!.View, refitRequest);
                Require(refitQuote.Blockers.IsEmpty && refitQuote.TotalPayment is { Value: > 0 }, "真实改装没有有效报价。");
                var refitBefore = EconomicState(host.GetStatus()!.View, serviceSettlement.Id);
                var refitContent = host.Content;
                var refitRejected = host.Submit(host.GetStatus()!.Binding,
                    new CoreCommand.CommissionVehicleWork(refitRequest, new(refitQuote.TotalPayment!.Value.Value - 1)),
                    CommandSource.AutomationTest, refitTarget);
                Require(refitRejected is SubmissionResult.Completed { Receipt.Decision: CommandDecision.Rejected rejectedRefit }
                    && rejectedRefit.Problems.Any(problem => problem.Code == "price_condition_failed")
                    && ReferenceEquals(refitContent, host.Content)
                    && refitBefore == EconomicState(host.GetStatus()!.View, serviceSettlement.Id), "拒绝改装改变了内容或经济状态：" + refitRejected);
                using var acceptedRefit = Success(host.ContentFactory.PrepareEdit(host.Content!, new ContentEdit.InstanceTarget(instance.Design, refitDefinition)));
                var acceptedChange = ((PreparedContentEdit.InstanceTarget)acceptedRefit.Edit!).Difference.Core;
                var refitWallet = host.GetStatus()!.View.Economy.Wallet.Value;
                var refitInventory = Found(host.GetStatus()!.View.FindInventory(serviceSettlement.Inventory));
                var refitSubmitted = host.Submit(host.GetStatus()!.Binding,
                    new CoreCommand.CommissionVehicleWork(refitRequest with { Change = acceptedChange }, refitQuote.TotalPayment.Value),
                    CommandSource.AutomationTest, acceptedRefit);
                Require(refitSubmitted is SubmissionResult.Completed { Receipt.Decision: CommandDecision.Accepted }
                    && ReferenceEquals(host.Content, acceptedRefit.View), "真实正式改装没有接受目标：" + refitSubmitted);
                var refitJob = ((SubmissionResult.Completed)refitSubmitted).Events.Select(e => e.Event).OfType<CoreEvent.ConstructionStarted>().Single().Job.Id;
                Require(host.GetStatus()!.View.Economy.Wallet.Value == refitWallet - refitQuote.TotalPayment.Value.Value,
                    "正式改装没有按报价扣款。");
                foreach (var purchase in refitQuote.NpcPurchases)
                    Require(Quantity(Found(host.GetStatus()!.View.FindInventory(serviceSettlement.Inventory)), purchase.Good)
                        == Quantity(refitInventory, purchase.Good) - purchase.Quantity, "正式改装重复或遗漏 NPC 扣料。");
                Require(Found(host.GetStatus()!.View.FindInstance(vehicleInstance)).Lock.HasFlag(InstanceLock.UnderRefit), "正式改装没有锁定实例。");
                var reservation = Found(host.GetStatus()!.View.FindInventory(lease.Inventory)).Reservations.Single(r => r.Job == refitJob);
                Require(reservation.Capacity > 0 && !reservation.ExpectedReturns.IsEmpty, "委托改装没有预约当地玩家租库返还。");
                await RestoreAndCompare("refit-working");
                Require(restored.Content!.FindBlueprint(refitBlueprint.Address) is ContentLookup<BlueprintRecord>.Found,
                    "真实恢复丢失改装蓝图。");
                CoreEvent.ConstructionCompleted? refitCompletion = null;
                host.BoundaryCompleted += (_, _, _, events) => refitCompletion ??= events.Select(e => e.Event)
                    .OfType<CoreEvent.ConstructionCompleted>().SingleOrDefault(e => e.Job == refitJob);
                for (var step = 0; step < 600 && host.GetStatus()!.View.FindJob(refitJob) is Lookup<JobView>.Found; step++)
                {
                    Require(host.Step(1).Completed == 1, "真实改装施工无法推进。");
                    if (step % 50 == 49) await TestScene.ToSignal(TestScene.GetTree(), SceneTree.SignalName.ProcessFrame);
                }
                instance = Found(host.GetStatus()!.View.FindInstance(vehicleInstance));
                Require(host.GetStatus()!.View.FindJob(refitJob) is Lookup<JobView>.Missing
                    && instance.Design == acceptedChange.Target.Design.Design && instance.Lock == InstanceLock.None,
                    "真实改装未完成、未更新设计或未解除锁定。");
                Require(refitCompletion is not null && refitCompletion.ReturnedGoods.SequenceEqual(reservation.ExpectedReturns),
                    "委托改装实际返还与核心预约不一致。");
                var returnedInventory = Found(host.GetStatus()!.View.FindInventory(lease.Inventory));
                Require(returnedInventory.Owner == new InventoryOwner.Lease(lease.Id) && returnedInventory.Reservations.All(r => r.Job != refitJob),
                    "委托返还没有保留当地玩家租库归属或清除预约。");
                foreach (var returned in refitCompletion!.ReturnedGoods)
                    Require(Quantity(returnedInventory, returned.Good) == Quantity(returnInventory, returned.Good) + returned.Quantity,
                        "委托改装没有恰好返还一次到当地玩家租库。");
            }
            var finalState = Saved(host).Core.Encode();
            var recording = Recorded(host);
            var package = RecordingPackage.Capture(recording.Window, recording.Content, recording.Runtime);
            var replayPath = Path.Combine(directory, "vehicle-service.recording.zip");
            package.Write(replayPath);
            package.Validate(replayPath);
            Require(restored.BeginReplay(replayPath).Problem is null, "真实录制文件重放请求被拒绝。");
            await Pump(restored);
            ReplayAdvanceResult? replay = null;
            for (var batch = 0; batch < 20 && replay?.Stop != ReplayStop.EndOfRecording; batch++)
            {
                replay = restored.AdvanceReplay(50);
                Require(replay is { Difference: null, Fault: null } && replay.Stop is ReplayStop.BudgetReached or ReplayStop.EndOfRecording,
                    "含拒绝目标依赖的严格重放失败：" + replay);
                await TestScene.ToSignal(TestScene.GetTree(), SceneTree.SignalName.ProcessFrame);
            }
            Require(replay?.Stop == ReplayStop.EndOfRecording && Saved(restored).Core.Encode() == finalState,
                "严格重放终态不一致，资金、库存、任务或编组可能重复应用。");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static T Success<T>(ContentResult<T> result) where T : notnull => result is ContentResult<T>.Success success
        ? success.Value : throw new InvalidOperationException("真实内容准备失败：" + result);
    private static T Found<T>(Lookup<T> result) where T : notnull => result is Lookup<T>.Found found
        ? found.Value : throw new InvalidOperationException("真实核心对象不存在：" + result);
    private static JobQuote Quote(ICoreReadView view, JobRequest request)
    {
        var result = view.GetJobQuote(request, JobFunding.NpcCommission);
        Require(result is CoreResult<JobQuote>.Success, "真实委托报价失败：" + result);
        return ((CoreResult<JobQuote>.Success)result).Value;
    }
    private static SaveCapture Saved(HostCoordinator host)
    {
        var saved = host.CaptureSave();
        if (saved.Failure is not null || saved.Capture is not { } capture)
            throw new InvalidOperationException("保存失败：" + saved.Failure?.Code + " " + saved.Failure?.Message);
        return capture;
    }
    private static RecordingSnapshot Recorded(HostCoordinator host)
    {
        var binding = host.GetStatus()!.Binding;
        if (host.ReadRecording(binding) is not { } window) throw new InvalidOperationException("没有可录制的窗口。");
        var recorded = host.CaptureRecording(binding, window);
        if (recorded.Failure is not null || recorded.Snapshot is not { } snapshot)
            throw new InvalidOperationException("录制失败：" + recorded.Failure?.Code + " " + recorded.Failure?.Message);
        return snapshot;
    }
    private static long Quantity(InventoryView inventory, GoodId good) => inventory.Stacks.SingleOrDefault(s => s.Good == good)?.Quantity ?? 0;
    private static string EconomicState(ICoreReadView view, SettlementId settlement)
    {
        var current = Found(view.FindSettlement(settlement));
        return view.Economy.Wallet.Value + "|" + string.Join(";", Found(view.FindInventory(current.Inventory)).Stacks.Select(s => $"{s.Good}:{s.Quantity}"))
            + "|" + string.Join(";", current.CommissionJobs);
    }
}
