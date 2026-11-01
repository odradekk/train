using System.Collections.Immutable;
using Godot;
using Train.App;
using Train.Content.Contracts;
using Train.Core.Contracts;
using static Godot.Control;
using static Train.Ui.EconomyPanels;

namespace Train.Ui;

internal static class EconomyPanelControls
{
    internal static bool Current(HostCoordinator host, HostBinding binding, HostBinding expectedBinding, ContentGeneration generation)
        => binding == expectedBinding && host.PanelGenerationCurrent(expectedBinding, generation);

    internal static void Invalidate(Control body, Label state)
    { state.Text = "已不存在：对象或所属对局已失效，请重新打开。"; state.AddThemeColorOverride("font_color", UiStyle.Error); Disable(body); }
    private static void Disable(Control body)
    {
        foreach (var button in body.FindChildren("*", "BaseButton", true, false).OfType<BaseButton>()) button.Disabled = true;
        foreach (var edit in body.FindChildren("*", "LineEdit", true, false).OfType<LineEdit>()) edit.Editable = false;
    }

    internal static T? ReadQuote<T>(CoreResult<T> result, Label description, IContentView content) where T : class
    {
        if (result is CoreResult<T>.Success success) return success.Value;
        description.Text = result switch
        {
            CoreResult<T>.Failure failure => "报价被拒绝：\n" + Problems(failure.Problems, content),
            CoreResult<T>.Faulted fault => CommandDiagnostics.Describe(fault.Fault),
            _ => throw new InvalidOperationException("未知报价结果。")
        };
        description.AddThemeColorOverride("font_color", UiStyle.Error); return null;
    }

    internal static void ShowQuote(ICoreReadView view, IContentView content, Label label, CoreRevision revision, string details, ImmutableArray<CoreProblem> blockers,
        ImmutableArray<CoreProblem> warnings = default, string? reason = null)
    {
        label.Text = "报价不锁定\n" + details +
            (revision == view.Revision ? "" : "\n报价后状态已变化，确认时由核心按当前状态判断。") +
            (blockers.IsEmpty ? "" : "\n受阻：\n" + Problems(blockers, content)) +
            (warnings.IsDefaultOrEmpty ? "" : "\n短缺风险：\n" + Problems(warnings, content)) +
            (reason is null ? "" : "\n" + reason);
        label.AddThemeColorOverride("font_color", !blockers.IsEmpty ? UiStyle.Error :
            !warnings.IsDefaultOrEmpty || reason is not null ? UiStyle.Warning : UiStyle.Foreground);
    }

    internal static void ResetQuote<T>(ref T? quote, Label label, string reason = "输入已改变，请获取报价。") where T : class
    { quote = null; label.Text = "报价不锁定。" + reason; label.AddThemeColorOverride("font_color", UiStyle.Foreground); }
    internal static void MissingSelection<T>(ref T? quote, Label label, string selection) where T : class =>
        ResetQuote(ref quote, label, $"已不存在：所选{selection}，请重新选择。");

    internal static Label Text(Node parent, string name, string value)
    {
        var label = UiStyle.Label(value); label.Name = name;
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart; label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        label.AddThemeFontOverride("font", UiStyle.NumericFont()); parent.AddChild(label); return label;
    }
    internal static VBoxContainer Page(TabContainer tabs, string title)
    {
        var page = new VBoxContainer { Name = title, SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        tabs.AddChild(page);
        var scroll = new ScrollContainer { Name = "Page", SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        page.AddChild(scroll);
        var body = new VBoxContainer { Name = "Body", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 10); scroll.AddChild(body); return body;
    }

    internal static HBoxContainer Footer(VBoxContainer body)
    {
        var footer = new HBoxContainer { Name = "Footer" };
        body.GetParent().GetParent().AddChild(footer);
        return footer;
    }
    internal static VBoxContainer List(Node parent, string name, int height)
    {
        var scroll = new ScrollContainer { Name = name + "Scroll", CustomMinimumSize = new(0, height), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        parent.AddChild(scroll); var rows = new VBoxContainer { Name = name, SizeFlagsHorizontal = SizeFlags.ExpandFill }; scroll.AddChild(rows); return rows;
    }
    internal static void Clear(Node node)
    { foreach (var child in node.GetChildren()) RemoveControl(child); }
    internal static void RemoveControl(Node node) { node.GetParent().RemoveChild(node); node.QueueFree(); }
    internal static void RemoveWindow(PanelContainer? panel) { if (panel is not null) RemoveControl(panel); }
    internal static string Problems(ImmutableArray<CoreProblem> problems, IContentView content) =>
        string.Join("\n", problems.Select(problem => CommandDiagnostics.Describe(problem, content)));
    internal static LineEdit IntegerInput(Node parent, string name, string placeholder, string initial = "")
    { var edit = new LineEdit { Name = name, Text = initial, PlaceholderText = placeholder }; edit.AddThemeFontOverride("font", UiStyle.NumericFont()); parent.AddChild(edit); return edit; }

    internal static string Amount(Money? money) => money?.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "不可报价";
}
