using System.Collections.Immutable;
using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Godot;
using Train.App;
using Train.App.Logging;
using Train.App.Notifications;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Scenes.Contracts;
using Train.Scenes.ContractSupport;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class CombatUiTests
{
    private static void Queue(ScriptedCoreSession session, ulong step, CoreEvent? item)
    {
        ImmutableArray<EventEnvelope> events = item is null ? [] : [new(new(step, 0), 0, item)];
        session.QueueAdvance(new(1, new(step, 0), events, null, null), new FixedCoreReadView(new(step, 0), HostCoreScenario.Input, null, new(1))
        {
            Economy = UiWorldFixture.Economy,
            World = new(0, new("fixture:railway"), 100, 100, [], [], [], [], [])
        });
    }

}
