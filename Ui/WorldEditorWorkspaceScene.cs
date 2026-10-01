using System.Globalization;
using Godot;
using Train.Domain.WorldEditor;
using Train.Domain.Voxels;

namespace Train;

public partial class WorldEditorWorkspaceScene : Control
{
    private WorldEditorWorkspace _workspace = null!;
    private VBoxContainer _saves = null!;
    private Label _result = null!;
    private SpinBox[] _bounds = null!;
    private LineEdit _seed = null!;
    internal IReadOnlyList<WorldEditorSaveEntry> Entries { get; private set; } = Array.Empty<WorldEditorSaveEntry>();
    internal string Result => _result.Text;

    public override void _Ready()
    {
        var main = GetNode<Main>("/root/Main");
        var reloader = main.Reloader!;
        _workspace = new WorldEditorWorkspace(reloader.Catalog, reloader.Composites, new FileWorldSaveStore(main.IsolatedUserDataDirectory));
        var margin = new MarginContainer { OffsetLeft = 24, OffsetTop = 24, OffsetRight = -24, OffsetBottom = -24 };
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(margin);
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 12);
        margin.AddChild(box);
        box.AddChild(new Label { Text = "世界编辑器 · 存档工作区" });
        var newWorld = new HBoxContainer();
        box.AddChild(newWorld);
        newWorld.AddChild(new Label { Text = "空白世界范围 X / Y / Z" });
        _bounds = new[] { BlankWorldSource.MaxSizeX, BlankWorldSource.MaxSizeY, BlankWorldSource.MaxSizeZ }
            .Select(max => new SpinBox { MinValue = BlankWorldSource.MinSize, MaxValue = max, Value = max, Rounded = true }).ToArray();
        foreach (var bound in _bounds)
            newWorld.AddChild(bound);
        newWorld.AddChild(new Label { Text = "种子" });
        _seed = new LineEdit { Text = "0", CustomMinimumSize = new Vector2(210, 0) };
        newWorld.AddChild(_seed);
        AddButton(newWorld, "新建空白世界", CreateBlank);
        var actions = new HBoxContainer();
        box.AddChild(actions);
        AddButton(actions, "刷新存档", RefreshSaves);
        AddButton(actions, "返回主菜单", () => GetTree().ChangeSceneToFile("res://Ui/StartMenu.tscn"));
        _result = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        box.AddChild(_result);
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        box.AddChild(scroll);
        _saves = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _saves.AddThemeConstantOverride("separation", 12);
        scroll.AddChild(_saves);
        RefreshSaves();
        GD.Print($"WORLD_EDITOR_WORKSPACE_READY saves={Entries.Count}");
        if (WorldEditorWorkspaceSmoke.Requested())
            Callable.From(() => WorldEditorWorkspaceSmoke.FromWorkspace(this)).CallDeferred();
    }

    internal void SetBlankInputs(int x, int y, int z, string seed)
    {
        _bounds[0].Value = x;
        _bounds[1].Value = y;
        _bounds[2].Value = z;
        _seed.Text = seed;
    }

    internal void CreateBlank()
    {
        if (!ulong.TryParse(_seed.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var seed) ||
            !BlankWorldSource.TryIdentity((int)_bounds[0].Value, (int)_bounds[1].Value, (int)_bounds[2].Value, seed, out var identity))
        {
            _result.Text = $"无法新建：范围须为 {BlankWorldSource.ExtentDescription}，种子须为 0..{ulong.MaxValue} 的整数。";
            return;
        }
        Enter(_workspace.NewBlank(identity));
    }

    internal WorldSaveResult OpenSave(Guid id)
    {
        var result = _workspace.TryOpen(id, out var candidate);
        if (candidate is null)
        {
            RefreshSaves();
            _result.Text = $"无法打开：{Describe(result)}";
        }
        else
            Enter(candidate);
        return result;
    }

    private void Enter(VoxelDevelopmentSession candidate)
    {
        var error = GetNode<Main>("/root/Main").EnterWorldEditor(candidate);
        if (error != Error.Ok)
            _result.Text = $"无法进入世界编辑器：{error}。";
    }

    internal void RefreshSaves()
    {
        foreach (var child in _saves.GetChildren())
        {
            _saves.RemoveChild(child);
            child.QueueFree();
        }
        Entries = _workspace.ListSaves();
        _result.Text = _workspace.ListFailure is { } failure ? $"存档列表读取失败：{failure.Detail}" : "选择存档打开，或新建空白世界。";
        if (Entries.Count == 0)
            _saves.AddChild(new Label { Text = "暂无本地存档。" });
        foreach (var entry in Entries)
        {
            var row = new HBoxContainer();
            _saves.AddChild(row);
            var saved = entry.LastSavedUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "未知";
            var hints = entry.ExternalDefinitions.Select(item => $"{item.DefinitionId} @ {item.SemanticRevision}")
                .Concat(entry.Source?.ContentDependencies.Select(item => $"{item.Kind}:{item.Id} @ {item.SemanticRevision}") ?? Array.Empty<string>())
                .Distinct().ToArray();
            var dependencies = hints.Length == 0 ? "无外部依赖" : string.Join("，", hints);
            var source = entry.Source is { } origin ? $"{origin.Kind}/{origin.ConfigurationId} v{origin.ConfigurationVersion}" : entry.GeneratorVersion;
            var text = $"{entry.Name} · 最近保存 {saved} · {entry.Id:D}\n格式 {entry.FormatVersion?.ToString() ?? "未知"} · {source}\n依赖：{dependencies}";
            if (!entry.Recovery.Succeeded)
                text += $"\n无法打开：{Describe(entry.Recovery)}";
            else if (entry.Recovery.Adaptations is { Count: > 0 } adaptations)
                text += "\n适配提示：" + string.Join("；", adaptations.Select(item => $"{item.Subject}/{item.Relationship}"));
            row.AddChild(new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = SizeFlags.ExpandFill });
            var button = AddButton(row, "打开", () => OpenSave(entry.Id));
            button.Disabled = !entry.Recovery.Succeeded;
        }
    }

    private static string Describe(WorldSaveResult result) =>
        $"{result.Status}" + (result.Diagnostic is { } reason ? $" · {reason.Stage}/{reason.Subject}/{reason.Relationship} · {reason.Detail}" : "");

    private static Button AddButton(Container parent, string text, Action action)
    {
        var button = new Button { Text = text };
        button.Pressed += action;
        parent.AddChild(button);
        return button;
    }
}
