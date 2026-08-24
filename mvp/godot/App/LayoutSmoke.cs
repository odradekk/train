using Godot;
using TrainMvp.Domain;
using TrainMvp.Planning;

namespace TrainMvp.App;

public static class LayoutSmoke
{
    public static List<string> Check(PlanningScreen planning, ScenarioData scenario)
    {
        var failures = new List<string>();
        Require(planning, "HudBar", 400, 36, failures);
        Require(planning, "HudLabel", 280, 20, failures, maxHeight: 48);
        Require(planning, "StartLoopButton", 108, 32, failures, horizontal: true);
        Require(planning, "ResetButton", 108, 32, failures, horizontal: true);
        Require(planning, "JoinLoopButton", 108, 32, failures, horizontal: true);
        Require(planning, "LeaveLoopButton", 108, 32, failures, horizontal: true);
        Require(planning, "SaveRuleButton", 108, 32, failures, horizontal: true);
        Require(planning, "RuleThresholdSpin", 180, 32, failures);
        Require(planning, "RuleTargetSpin", 180, 32, failures);
        Require(planning, "RuleGoodOption", 160, 28, failures, horizontal: true);
        Require(planning, "RuleSideOption", 160, 28, failures, horizontal: true);
        Require(planning, "OperationHint", 240, 16, failures, maxHeight: 40);
        foreach (var stationId in scenario.StationOrder)
        {
            Require(planning, $"StationCard_{stationId}", 150, 82, failures);
            Require(planning, $"StationName_{stationId}", 28, 16, failures, horizontal: true, maxHeight: 36);
        }

        return failures;
    }

    public static List<string> CheckTwelveStopLoop(PlanningScreen planning)
    {
        var failures = new List<string>();
        var scroll = planning.FindChild("LoopRowsScroll", true, false) as ScrollContainer;
        if (scroll is null)
        {
            failures.Add("LoopRowsScroll missing");
            return failures;
        }

        var viewport = planning.GetViewport().GetVisibleRect();
        var scrollRect = scroll.GetGlobalRect();
        GD.Print($"LAYOUT_SMOKE_LOOP_SCROLL y={scrollRect.Position.Y:0} h={scrollRect.Size.Y:0} view={viewport.Size.Y:0}");
        if (scrollRect.Position.Y < 0 || scrollRect.End.Y > viewport.Size.Y + 1)
        {
            failures.Add($"LoopRowsScroll {scrollRect} overflows viewport {viewport.Size}");
        }

        var rows = planning.FindChild("LoopRows", true, false);
        if (rows is null || rows.GetChildCount() != 12)
        {
            failures.Add($"LoopRows has {rows?.GetChildCount() ?? 0} children, expected 12");
            return failures;
        }

        if (rows is Control content && content.Size.Y + 0.5f <= scroll.Size.Y)
        {
            failures.Add($"12-stop LoopRows {content.Size.Y} should exceed scroll {scroll.Size.Y}");
        }

        Require(planning, "HudBar", 400, 36, failures);
        Require(planning, "PlanSummary", 80, 16, failures);
        Require(planning, "PathSummary", 80, 16, failures);
        return failures;
    }

    public static List<string> CheckLoopRowReachable(PlanningScreen planning, int index, string label)
    {
        var failures = new List<string>();
        var row = planning.FindChild($"LoopRow_{index}", true, false) as Control;
        if (row is null)
        {
            failures.Add($"{label} LoopRow_{index} missing");
            return failures;
        }

        var viewport = planning.GetViewport().GetVisibleRect();
        var rect = row.GetGlobalRect();
        GD.Print($"LAYOUT_SMOKE_LOOP_ROW {label} {index} y={rect.Position.Y:0} h={rect.Size.Y:0}");
        if (!viewport.Intersects(rect))
        {
            failures.Add($"{label} LoopRow_{index} {rect} not in viewport {viewport.Size}");
        }

        Require(planning, $"LoopRowUp_{index}", 72, 32, failures, horizontal: true);
        Require(planning, $"LoopRowDown_{index}", 72, 32, failures, horizontal: true);
        Require(planning, $"LoopRowDelete_{index}", 72, 32, failures, horizontal: true);
        return failures;
    }

    public static void ScrollLoopRows(PlanningScreen planning, bool toEnd)
    {
        if (planning.FindChild("LoopRowsScroll", true, false) is not ScrollContainer scroll)
        {
            return;
        }

        if (toEnd && planning.FindChild("LoopRows", true, false) is Control content)
        {
            scroll.ScrollVertical = (int)Math.Max(0, content.Size.Y - scroll.Size.Y);
            return;
        }

        scroll.ScrollVertical = 0;
    }

    public static string? TrySavePlanningShot(Viewport viewport, string path)
    {
        if (DisplayServer.GetName() == "headless")
        {
            return null;
        }

        var texture = viewport.GetTexture();
        Image? image = null;
        try
        {
            image = texture?.GetImage();
        }
        catch (Exception)
        {
            return null;
        }

        if (image is null)
        {
            return null;
        }

        DirAccess.MakeDirRecursiveAbsolute(path.GetBaseDir());
        var error = image.SavePng(path);
        return error == Error.Ok ? path : null;
    }

    private static void Require(
        Node root,
        string name,
        float minWidth,
        float minHeight,
        List<string> failures,
        bool horizontal = false,
        float? maxHeight = null)
    {
        var node = root.FindChild(name, true, false);
        if (node is not Control control)
        {
            failures.Add($"{name} missing");
            return;
        }

        var size = control.Size;
        if (size.X + 0.5f < minWidth || size.Y + 0.5f < minHeight)
        {
            failures.Add($"{name} {size} smaller than {minWidth}x{minHeight}");
        }

        if (maxHeight is not null && size.Y > maxHeight.Value + 0.5f)
        {
            failures.Add($"{name} height {size.Y} exceeds {maxHeight}");
        }

        if (horizontal && size.X + 0.5f < size.Y)
        {
            failures.Add($"{name} collapsed to a vertical bar {size}");
        }

        GD.Print($"LAYOUT_SMOKE_RECT {name} {size.X:0}x{size.Y:0}");
    }
}
