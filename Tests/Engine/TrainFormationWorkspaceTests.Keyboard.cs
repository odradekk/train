using System.Collections.Immutable;
using Chickensoft.GoDotTest;
using Godot;
using Train.Core.Contracts;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class TrainFormationWorkspaceTests
{
    [Test]
    public async Task Repeated_keyboard_moves_keep_the_pressed_direction_until_the_boundary()
    {
        var view = LongWorld(1);
        view.FormationPreviews = new[] { Array.Empty<ulong>(), new ulong[] { 2 }, [2, 3], [2, 3, 1], [2, 1, 3], [1, 2, 3] }
            .Select(ids => ((TrainId?)null, ids.Select(id => new VehicleId(id)).ToImmutableArray(),
                (CoreResult<TrainFormationPreview>)new CoreResult<TrainFormationPreview>.Success(new(12, 34, 56, 78, [], null))));
        await WithWorkspace([], async (panel, binding, current, content) =>
        {
            Open(panel, binding, current, content);
            var input = new UiTestInput(TestScene);
            foreach (var id in new[] { 2, 3, 1 }) await input.Activate(Find<CheckBox>(panel, $"Vehicle{id}"));
            var ordered = Find<VBoxContainer>(panel, "OrderedVehicles");
            var vehicle = ordered.GetChild(2);
            var up = Find<Button>(vehicle, "MoveUp");
            up.GrabFocus();
            await input.PressKey(Key.Space);
            Require(ordered.GetChild(1) == vehicle && up.HasFocus() && !up.Disabled,
                "未到边界时，前移应保留在原按钮上的焦点。");
            await input.PressKey(Key.Space);
            Require(ordered.GetChildCount() == 3 && ordered.GetChild(0) == vehicle &&
                Find<Button>(vehicle, "MoveDown").HasFocus(), "第二次空格未继续前移同一车辆并在边界切换方向。");
        }, view);
    }

    [Test]
    public async Task Repeated_keyboard_reordering_keeps_members_and_focuses_the_available_direction_at_boundaries()
    {
        await WithWorkspace([], async (panel, binding, view, content) =>
        {
            panel.Open(new(binding, content.Generation, new WorldObjectId.Node(new(1))), new NodeId(1), view, content);
            var input = new UiTestInput(TestScene);
            await input.Activate(Find<CheckBox>(panel, "Vehicle1"));
            await input.Activate(Find<CheckBox>(panel, "Vehicle2"));
            var ordered = Find<VBoxContainer>(panel, "OrderedVehicles");
            var vehicle = ordered.GetChild(1);
            var up = Find<Button>(vehicle, "MoveUp");
            var down = Find<Button>(vehicle, "MoveDown");
            up.GrabFocus();

            await input.PressKey(Key.Space);
            Require(ordered.GetChild(0) == vehicle, "第一次空格应把第二辆车前移到首位。");
            var focusedDownAtFront = down.HasFocus() && !down.Disabled;

            await input.PressKey(Key.Space);
            Require(ordered.GetChildCount() == 2 && Find<CheckBox>(panel, "Vehicle1").ButtonPressed &&
                Find<CheckBox>(panel, "Vehicle2").ButtonPressed,
                "连续按空格调整顺序不得把车辆移出编组。");
            Require(focusedDownAtFront, "到达首位后，应聚焦同一辆车仍可用的后移按钮。");
            Require(ordered.GetChild(1) == vehicle, "第二次空格应把同一辆车后移回末位。");
            Require(up.HasFocus() && !up.Disabled, "到达末位后，应聚焦同一辆车仍可用的前移按钮。");
        });
    }
}
