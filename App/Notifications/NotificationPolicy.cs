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
    RunDestroyed
}

internal sealed record NotificationNotice(
    NotificationCategory Category,
    NotificationLevel Level,
    string Text,
    WorldLocation? Location,
    WorldObjectId? Subject);

internal sealed class NotificationPolicy
{
    internal const string RunDestroyedText =
        "运行因车辆全部被毁结束。游戏继续。可以在「存档」里读档，或「返回菜单」后新游戏。";

    private readonly NotificationAction[] _actions = new NotificationAction[Enum.GetValues<NotificationCategory>().Length];
    private ICoreReadView? _previous;

    public NotificationPolicy() => _actions[(int)NotificationCategory.Encounter] = NotificationAction.SlowToOne;

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
        _ => throw new ArgumentOutOfRangeException(nameof(category))
    };

    internal NotificationAction Action(NotificationCategory category) => _actions[(int)category];

    internal void SetAction(NotificationCategory category, NotificationAction action)
    {
        if (!Enum.IsDefined(category)) throw new ArgumentOutOfRangeException(nameof(category));
        if (!Enum.IsDefined(action)) throw new ArgumentOutOfRangeException(nameof(action));
        _actions[(int)category] = action;
    }

    internal void Observe(ICoreReadView view) => _previous = view;

    internal NotificationAction Decide(CoreEvent @event) =>
        Classify(@event, null) is { } notice ? Action(notice.Category) : NotificationAction.None;

    internal NotificationNotice? Classify(CoreEvent @event, ICoreReadView? current) => @event switch
    {
        CoreEvent.CombatStarted started => Encounter("遭遇战斗。", started.Combat, started.Location),
        CoreEvent.CombatJoined joined => Encounter("有单位加入战斗。", joined.Combat, CombatLocation(current, joined.Combat)),
        CoreEvent.CombatDestruction destruction => Destruction(destruction),
        CoreEvent.CombatRetreatStarted retreat => Notice(NotificationCategory.RetreatStarted, NotificationLevel.Warning,
            "开始撤退。", null, UnitSubject(retreat.Unit)),
        CoreEvent.RetreatCompleted completed => Notice(NotificationCategory.RetreatCompleted, NotificationLevel.Completion,
            "撤退到达。", new WorldLocation.Node(completed.Node), new WorldObjectId.Train(completed.Train)),
        CoreEvent.CombatEnded ended => Notice(NotificationCategory.CombatEnded, NotificationLevel.Completion,
            "战斗结束。", ended.Location, new WorldObjectId.Combat(ended.Combat)),
        CoreEvent.SettlementSurrendered surrendered => Notice(NotificationCategory.Surrender, NotificationLevel.Warning,
            "聚居地投降。", null, new WorldObjectId.Settlement(surrendered.Settlement)),
        CoreEvent.LairCleared cleared => Notice(NotificationCategory.LairCleared, NotificationLevel.Completion,
            "生物聚落被清剿。", new WorldLocation.Node(cleared.Node), new WorldObjectId.Lair(cleared.Lair)),
        CoreEvent.SettlementRelationChanged changed => Relation(changed),
        CoreEvent.RunReported { Report.Reason: RunEndReason.AllVehiclesDestroyed } reported =>
            Notice(NotificationCategory.RunDestroyed, NotificationLevel.Severe, RunDestroyedText, null,
                new WorldObjectId.Train(reported.Report.Train)),
        _ => null
    };

    private NotificationNotice Encounter(string text, CombatId combat, WorldLocation? location) =>
        Notice(NotificationCategory.Encounter, NotificationLevel.Warning, text, location, new WorldObjectId.Combat(combat));

    private NotificationNotice Destruction(CoreEvent.CombatDestruction destruction) =>
        Notice(NotificationCategory.Destruction, DestructionLevel(destruction), "单位或部件被摧毁。",
            destruction.Location, new WorldObjectId.Combat(destruction.Combat));

    private NotificationLevel DestructionLevel(CoreEvent.CombatDestruction destruction)
    {
        var sawInstance = false;
        var missing = false;
        foreach (var unit in destruction.Units)
            if (unit is DestroyedUnit.Instance instance && Inspect(instance.Value.Instance, ref sawInstance, ref missing))
                return NotificationLevel.Severe;
        foreach (var part in destruction.Parts)
            if (Inspect(part.Instance.Instance, ref sawInstance, ref missing))
                return NotificationLevel.Severe;
        if (!sawInstance) return NotificationLevel.Information;
        return missing ? NotificationLevel.Severe : NotificationLevel.Information;
    }

    private bool Inspect(InstanceId id, ref bool sawInstance, ref bool missing)
    {
        sawInstance = true;
        if (_previous?.FindInstance(id) is not Lookup<InstanceView>.Found found)
        {
            missing = true;
            return false;
        }
        return found.Value.Ownership == SettlementRelation.PlayerOwned;
    }

    private static NotificationNotice? Relation(CoreEvent.SettlementRelationChanged changed) => changed.After switch
    {
        // 批准表只规定变为自有和变为敌对。
        SettlementRelation.PlayerOwned => Notice(NotificationCategory.Relation, NotificationLevel.Completion,
            "聚居地变为自有。", null, new WorldObjectId.Settlement(changed.Settlement)),
        SettlementRelation.Hostile => Notice(NotificationCategory.Relation, NotificationLevel.Warning,
            "聚居地变为敌对。", null, new WorldObjectId.Settlement(changed.Settlement)),
        _ => null
    };

    private static WorldLocation? CombatLocation(ICoreReadView? current, CombatId combat) =>
        current?.World.Combats.FirstOrDefault(item => item.Id == combat)?.Location;

    private static WorldObjectId UnitSubject(CombatUnitId unit) => unit switch
    {
        CombatUnitId.Instance instance => new WorldObjectId.Instance(instance.Id),
        CombatUnitId.CreatureGroup group => new WorldObjectId.CreatureGroup(group.Id),
        _ => throw new ArgumentOutOfRangeException(nameof(unit))
    };

    private static NotificationNotice Notice(NotificationCategory category, NotificationLevel level, string text,
        WorldLocation? location, WorldObjectId? subject) => new(category, level, text, location, subject);
}
