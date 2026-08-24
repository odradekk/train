using Godot;
using TrainMvp.Domain;
using TrainMvp.Planning;

namespace TrainMvp.App;

public static class LayoutSmoke
{
    public static List<string> CheckLogicalViewport(Viewport viewport, int width, int height)
    {
        var failures = new List<string>();
        var size = viewport.GetVisibleRect().Size;
        GD.Print($"LAYOUT_SMOKE_VIEWPORT {size.X:0}x{size.Y:0} expect {width}x{height}");
        if (Math.Abs(size.X - width) > 2 || Math.Abs(size.Y - height) > 2)
        {
            failures.Add($"logical viewport {size} is not {width}x{height}");
        }

        return failures;
    }

    public static List<string> Check1080UsesSpace(PlanningScreen planning)
    {
        var failures = new List<string>();
        if (planning.FindChild("LoopRowsScroll", true, false) is not ScrollContainer scroll)
        {
            failures.Add("LoopRowsScroll missing");
            return failures;
        }

        GD.Print($"LAYOUT_SMOKE_1080_LOOP_SCROLL {scroll.Size.X:0}x{scroll.Size.Y:0}");
        if (scroll.Size.Y + 0.5f < 160)
        {
            failures.Add($"1080p LoopRowsScroll height {scroll.Size.Y} still a narrow 720p gutter");
        }

        if (planning.FindChild("InspectorScroll", true, false) is Control inspector && inspector.Size.X + 0.5f < 500)
        {
            failures.Add($"1080p inspector width {inspector.Size.X} did not use extra space");
        }

        return failures;
    }

    public static List<string> Check1080RunUsesSpace(Control run)
    {
        var failures = new List<string>();
        if (run.FindChild("RunSidePanel", true, false) is not Control side)
        {
            failures.Add("RunSidePanel missing");
            return failures;
        }

        GD.Print($"LAYOUT_SMOKE_1080_RUN_SIDE {side.Size.X:0}x{side.Size.Y:0}");
        if (side.Size.X + 0.5f < 500)
        {
            failures.Add($"1080p run side {side.Size.X} is a collapsed gutter");
        }

        return failures;
    }

    public static List<string> Check1080ReportUsesSpace(Control run)
    {
        var failures = new List<string>();
        failures.AddRange(Check1080RunUsesSpace(run));
        if (run.FindChild("ReportPanel", true, false) is not Control report)
        {
            failures.Add("ReportPanel missing");
            return failures;
        }

        GD.Print($"LAYOUT_SMOKE_1080_REPORT {report.Size.X:0}x{report.Size.Y:0} visible={report.Visible}");
        if (!report.Visible)
        {
            failures.Add("ReportPanel is hidden at 1080p report");
        }

        if (report.Size.X + 0.5f < 500 || report.Size.Y + 0.5f < 240)
        {
            failures.Add($"1080p ReportPanel {report.Size} is too small to read");
        }

        return failures;
    }

    public static List<string> CheckFinishedObservation(Control run)
    {
        var failures = new List<string>();
        if (run.FindChild("ReportPanel", true, false) is Control report && report.Visible && report.IsVisibleInTree())
        {
            failures.Add("ReportPanel is still visible after close");
        }

        if (run.FindChild("FinishedReturnButton", true, false) is not BaseButton back)
        {
            failures.Add("FinishedReturnButton missing");
            return failures;
        }

        GD.Print($"LAYOUT_SMOKE_FINISHED_RETURN visible={back.IsVisibleInTree()} size={back.Size.X:0}x{back.Size.Y:0}");
        if (!back.Visible || !back.IsVisibleInTree())
        {
            failures.Add("FinishedReturnButton is not visible after close");
        }

        if (back.Disabled)
        {
            failures.Add("FinishedReturnButton is disabled");
        }

        return failures;
    }

    public static string? PressNamedButton(Node root, string name)
    {
        if (root.FindChild(name, true, false) is not BaseButton button)
        {
            return $"{name} missing";
        }

        if (!button.Visible || !button.IsVisibleInTree())
        {
            return $"{name} is not visible";
        }

        if (button.Disabled)
        {
            return $"{name} is disabled";
        }

        button.EmitSignal(BaseButton.SignalName.Pressed);
        return null;
    }

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
