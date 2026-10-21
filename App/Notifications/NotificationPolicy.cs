using Train.Core.Contracts;

namespace Train.App.Notifications;

internal enum NotificationLevel { Information, Completion, Warning, Severe }

internal enum NotificationCategory
{
    Encounter,
    Destruction,
    RetreatStarted,
    RetreatCompleted,
    CombatEnded,
    Surrender,
    LairCleared,
    Relation,
    RunDestroyed,
    Population,
    Construction,
    Railway,
    TrainOperation,
    Resource,
    Lease,
    Founding
}

internal sealed record NotificationNotice(
    NotificationCategory Category,
    NotificationLevel Level,
    string Text,
    WorldLocation? Location,
    WorldObjectId? Subject,
    GoodId? Good = null);

internal sealed class NotificationPolicy
{
    internal const string RunDestroyedText =
        "运行因车辆全部被毁结束。游戏继续。可以在「存档」里读档，或「返回菜单」后新游戏。";

    private readonly NotificationAction[] _actions = new NotificationAction[Enum.GetValues<NotificationCategory>().Length];

    public NotificationPolicy()
    {
        foreach (var category in Enum.GetValues<NotificationCategory>())
            _actions[(int)category] = DefaultAction(category);
    }

    internal static NotificationAction DefaultAction(NotificationCategory category) =>
        category == NotificationCategory.Encounter ? NotificationAction.SlowToOne : NotificationAction.None;

    internal static string Key(NotificationCategory category) => category switch
    {
        NotificationCategory.Encounter => "encounter",
        NotificationCategory.Destruction => "destruction",
        NotificationCategory.RetreatStarted => "retreat_started",
        NotificationCategory.RetreatCompleted => "retreat_completed",
        NotificationCategory.CombatEnded => "combat_ended",
        NotificationCategory.Surrender => "surrender",
        NotificationCategory.LairCleared => "lair_cleared",
        NotificationCategory.Relation => "relation",
        NotificationCategory.RunDestroyed => "run_destroyed",
        NotificationCategory.Population => "population",
        NotificationCategory.Construction => "construction",
        NotificationCategory.Railway => "railway",
        NotificationCategory.TrainOperation => "train_operation",
        NotificationCategory.Resource => "resource",
        NotificationCategory.Lease => "lease",
        NotificationCategory.Founding => "founding",
        _ => throw new ArgumentOutOfRangeException(nameof(category))
    };

    internal static string Label(NotificationCategory category) => category switch
    {
        NotificationCategory.Encounter => "遇敌",
        NotificationCategory.Destruction => "摧毁",
        NotificationCategory.RetreatStarted => "开始撤退",
        NotificationCategory.RetreatCompleted => "撤退到达",
        NotificationCategory.CombatEnded => "战斗结束",
        NotificationCategory.Surrender => "投降",
        NotificationCategory.LairCleared => "清剿",
        NotificationCategory.Relation => "关系变化",
        NotificationCategory.RunDestroyed => "列车全毁",
        NotificationCategory.Population => "人口",
        NotificationCategory.Construction => "施工",
        NotificationCategory.Railway => "铁路",
        NotificationCategory.TrainOperation => "列车运行",
        NotificationCategory.Resource => "资源",
        NotificationCategory.Lease => "租约",
        NotificationCategory.Founding => "建城",
        _ => throw new ArgumentOutOfRangeException(nameof(category))
    };

    internal NotificationAction Action(NotificationCategory category) => _actions[(int)category];

    internal void SetAction(NotificationCategory category, NotificationAction action)
    {
        if (!Enum.IsDefined(category)) throw new ArgumentOutOfRangeException(nameof(category));
        if (!Enum.IsDefined(action)) throw new ArgumentOutOfRangeException(nameof(action));
        _actions[(int)category] = action;
    }

    internal NotificationAction Decide(CoreEvent @event) =>
        Classify(@event, null, null) is { } notice ? Action(notice.Category) : NotificationAction.None;

    internal NotificationNotice? Classify(CoreEvent @event, ICoreReadView? previous, ICoreReadView? current) => @event switch
    {
        CoreEvent.CombatStarted started => Encounter("遭遇战斗。", started.Combat, started.Location),
        CoreEvent.CombatJoined joined => Encounter("有单位加入战斗。", joined.Combat, CombatLocation(current, joined.Combat)),
        CoreEvent.CombatDestruction destruction => Destruction(destruction, previous),
        CoreEvent.CombatRetreatStarted retreat => Notice(NotificationCategory.RetreatStarted, NotificationLevel.Warning,
            "开始撤退。", null, UnitSubject(retreat.Unit, current)),
        CoreEvent.RetreatCompleted completed => Notice(NotificationCategory.RetreatCompleted, NotificationLevel.Completion,
            "撤退到达。", new WorldLocation.Node(completed.Node), new WorldObjectId.Train(completed.Train)),
        CoreEvent.CombatEnded ended => Notice(NotificationCategory.CombatEnded, NotificationLevel.Completion,
            "战斗结束。", ended.Location, null),
        CoreEvent.SettlementSurrendered surrendered => Notice(NotificationCategory.Surrender, NotificationLevel.Warning,
            "投降。", null, new WorldObjectId.Settlement(surrendered.Settlement)),
        CoreEvent.LairCleared cleared => Notice(NotificationCategory.LairCleared, NotificationLevel.Completion,
            "生物聚落被清剿。", new WorldLocation.Node(cleared.Node), null),
        CoreEvent.SettlementRelationChanged changed => Relation(changed),
        CoreEvent.RunReported { Report.Reason: RunEndReason.AllVehiclesDestroyed } reported =>
            Notice(NotificationCategory.RunDestroyed, NotificationLevel.Severe, RunDestroyedText, null,
                new WorldObjectId.Train(reported.Report.Train)),
        CoreEvent.RunReported { Report.Reason: RunEndReason.PlanCompleted } reported =>
            Notice(NotificationCategory.TrainOperation, NotificationLevel.Completion, "运行计划走完。", null,
                new WorldObjectId.Train(reported.Report.Train)),
        CoreEvent.TrainActivityChanged { After: TrainActivity.NoRoute } blocked =>
            Notice(NotificationCategory.TrainOperation, NotificationLevel.Warning, "受阻：没有可达路径。", null,
                new WorldObjectId.Train(blocked.Train)),
        CoreEvent.PopulationShortageChanged changed => Notice(NotificationCategory.Population,
            changed.Shortage ? NotificationLevel.Warning : NotificationLevel.Completion,
            $"{NeedName(changed.Need)}{(changed.Shortage ? "短缺" : "短缺恢复")}。", null,
            new WorldObjectId.Settlement(changed.Settlement)),
        CoreEvent.PopulationMinimumReached minimum => Notice(NotificationCategory.Population, NotificationLevel.Severe,
            "人口降至最低。", null, new WorldObjectId.Settlement(minimum.Settlement)),
        CoreEvent.ConstructionCompleted completed => Notice(NotificationCategory.Construction, NotificationLevel.Completion,
            $"{KindName(completed.Kind)}完成。", null, CompletedSubject(completed, previous)),
        CoreEvent.ConstructionStateChanged { After: ConstructionState.ResourceBlocked or ConstructionState.Failed } changed =>
            Notice(NotificationCategory.Construction, NotificationLevel.Warning,
                changed.After == ConstructionState.Failed ? "施工失败。" : "施工缺料。", null, JobSettlement(changed.Job, current)),
        CoreEvent.RailBroken broken => Notice(NotificationCategory.Railway, NotificationLevel.Warning, "铁轨断开。",
            new WorldLocation.Edge(broken.Edge, broken.Break.DistanceMeters), null),
        CoreEvent.RailRepaired repaired => Notice(NotificationCategory.Railway, NotificationLevel.Completion, "铁轨修复。",
            EdgeMiddle(repaired.Edge, current), null),
        CoreEvent.RailExpanded expanded => Notice(NotificationCategory.Railway, NotificationLevel.Completion,
            $"线路扩建到 {expanded.Tracks} 条轨。", EdgeMiddle(expanded.Edge, current), null),
        CoreEvent.RailwayWorkChanged changed => Notice(NotificationCategory.Railway, NotificationLevel.Warning,
            "铁路施工受阻。", EdgeMiddle(changed.Edge, current), null),
        // 可再生资源在开采快于恢复时会反复归零又恢复，只通知不可再生资源的耗尽。
        CoreEvent.ResourceExhausted { Renewable: false } exhausted => Notice(NotificationCategory.Resource,
            NotificationLevel.Warning, "资源耗尽。", new WorldLocation.Node(exhausted.Node), null, exhausted.Good),
        CoreEvent.ResourceRegenerated regenerated => Notice(NotificationCategory.Resource, NotificationLevel.Information,
            "资源再生。", new WorldLocation.Node(regenerated.Node), null, regenerated.Good),
        CoreEvent.LeaseExpired expired => Notice(NotificationCategory.Lease, NotificationLevel.Warning, "仓库租约到期。",
            null, new WorldObjectId.Settlement(expired.Settlement)),
        CoreEvent.SettlementFounded founded => Notice(NotificationCategory.Founding, NotificationLevel.Completion,
            "建城完成。", null, new WorldObjectId.Settlement(founded.Settlement)),
        _ => null
    };

    private static string NeedName(PopulationNeed need) => need switch
    {
        PopulationNeed.Food => "主食", PopulationNeed.Clothing => "衣物", PopulationNeed.ImprovedDiet => "改善饮食",
        PopulationNeed.ConsumerGoods => "日用品", PopulationNeed.ConsumerElectronics => "消费电子",
        PopulationNeed.MedicalSupplies => "医疗用品", _ => need.ToString()
    };

    private static string KindName(ConstructionKind kind) => kind switch
    {
        ConstructionKind.Construction => "建造", ConstructionKind.Refit => "改装", ConstructionKind.Repair => "修理",
        ConstructionKind.Deconstruction => "拆解", _ => kind.ToString()
    };

    private static WorldObjectId? CompletedSubject(CoreEvent.ConstructionCompleted completed, ICoreReadView? previous) =>
        completed switch
        {
            { CreatedInstance: { } created } => new WorldObjectId.Instance(created),
            { Kind: not ConstructionKind.Deconstruction, Target: ConstructionTarget.Existing existing } =>
                new WorldObjectId.Instance(existing.Instance),
            _ => JobSettlement(completed.Job, previous)
        };

    private static WorldObjectId? JobSettlement(JobId job, ICoreReadView? view) =>
        view?.FindJob(job) is Lookup<JobView>.Found { Value: JobView.Construction construction }
            ? new WorldObjectId.Settlement(construction.Value.Settlement) : null;

    private static WorldLocation EdgeMiddle(EdgeId edge, ICoreReadView? view) => new WorldLocation.Edge(edge,
        view?.FindEdge(edge) is Lookup<EdgeView>.Found found ? found.Value.Map.LengthMeters / 2 : 0);

    private NotificationNotice Encounter(string text, CombatId combat, WorldLocation? location) =>
        Notice(NotificationCategory.Encounter, NotificationLevel.Warning, text, location, new WorldObjectId.Combat(combat));

    private static NotificationNotice Destruction(CoreEvent.CombatDestruction destruction, ICoreReadView? previous) =>
        Notice(NotificationCategory.Destruction, DestructionLevel(destruction, previous), "单位或部件被摧毁。",
            destruction.Location, new WorldObjectId.Combat(destruction.Combat));

    private static NotificationLevel DestructionLevel(CoreEvent.CombatDestruction destruction, ICoreReadView? previous)
    {
        var sawInstance = false;
        var missing = false;
        foreach (var unit in destruction.Units)
            if (unit is DestroyedUnit.Instance instance && Inspect(previous, instance.Value.Instance, ref sawInstance, ref missing))
                return NotificationLevel.Severe;
        foreach (var part in destruction.Parts)
            if (Inspect(previous, part.Instance.Instance, ref sawInstance, ref missing))
                return NotificationLevel.Severe;
        if (!sawInstance) return NotificationLevel.Information;
        return missing ? NotificationLevel.Severe : NotificationLevel.Information;
    }

    private static bool Inspect(ICoreReadView? previous, InstanceId id, ref bool sawInstance, ref bool missing)
    {
        sawInstance = true;
        if (previous?.FindInstance(id) is not Lookup<InstanceView>.Found found)
        {
            missing = true;
            return false;
        }
        return found.Value.Ownership == SettlementRelation.PlayerOwned;
    }

    private static NotificationNotice? Relation(CoreEvent.SettlementRelationChanged changed) => changed.After switch
    {
        SettlementRelation.PlayerOwned => Notice(NotificationCategory.Relation, NotificationLevel.Completion,
            "变为自有。", null, new WorldObjectId.Settlement(changed.Settlement)),
        SettlementRelation.Hostile => Notice(NotificationCategory.Relation, NotificationLevel.Warning,
            "变为敌对。", null, new WorldObjectId.Settlement(changed.Settlement)),
        _ => null
    };

    private static WorldLocation? CombatLocation(ICoreReadView? current, CombatId combat) =>
        current?.World.Combats.FirstOrDefault(item => item.Id == combat)?.Location;

    // 列车的每辆车各发一条撤退事件；主体归到列车，同一边界的相同通知只显示一次。
    private static WorldObjectId UnitSubject(CombatUnitId unit, ICoreReadView? current) => unit switch
    {
        CombatUnitId.Instance instance when current?.FindInstance(instance.Id) is Lookup<InstanceView>.Found
            { Value.Details: InstanceDetails.Vehicle { Train: { } train } } => new WorldObjectId.Train(train),
        CombatUnitId.Instance instance => new WorldObjectId.Instance(instance.Id),
        CombatUnitId.CreatureGroup group => new WorldObjectId.CreatureGroup(group.Id),
        _ => throw new ArgumentOutOfRangeException(nameof(unit))
    };

    private static NotificationNotice Notice(NotificationCategory category, NotificationLevel level, string text,
        WorldLocation? location, WorldObjectId? subject, GoodId? good = null) => new(category, level, text, location, subject, good);
}
