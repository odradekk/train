using System.Globalization;
using Godot;
using Train.Domain.WorldEditor;
using Train.Domain.Voxels;

namespace Train;

public partial class WorldEditorWorkspaceScene : Control
{
    private WorldEditorWorkspace _workspace = null!;
    private VBoxContainer _saves = null!;
    private VBoxContainer _examples = null!;
    private Label _result = null!;
    private SpinBox[] _bounds = null!;
    private LineEdit _seed = null!;
    private FileDialog _importFile = null!;
    internal IReadOnlyList<WorldEditorSaveEntry> Entries { get; private set; } = Array.Empty<WorldEditorSaveEntry>();
    internal IReadOnlyList<WorldEditorExampleEntry> ExampleEntries { get; private set; } = Array.Empty<WorldEditorExampleEntry>();
    internal string Result => _result.Text;

    public override void _Ready()
    {
        var main = GetNode<Main>("/root/Main");
        var reloader = main.Reloader!;
        _workspace = new WorldEditorWorkspace(reloader.Catalog, reloader.Composites, new FileWorldSaveStore(main.IsolatedUserDataDirectory), ReadExample);
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
        AddButton(actions, "导入 JSON 存档", () => _importFile.PopupCentered(new Vector2I(820, 560)));
        AddButton(actions, "返回主菜单", () => main.ReturnToMainMenu());
        _importFile = new FileDialog
        {
            FileMode = FileDialog.FileModeEnum.OpenFile, Access = FileDialog.AccessEnum.Filesystem,
            Filters = new[] { "*.json ; JSON 存档" }, ModeOverridesTitle = false, Title = "导入单个 JSON 存档"
        };
        _importFile.FileSelected += path => ImportSave(path);
        AddChild(_importFile);
        _result = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        box.AddChild(_result);
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        box.AddChild(scroll);
        var lists = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        scroll.AddChild(lists);
        lists.AddChild(new Label { Text = "随附示例" });
        _examples = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        lists.AddChild(_examples);
        lists.AddChild(new Label { Text = "本地存档" });
        _saves = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _saves.AddThemeConstantOverride("separation", 12);
        lists.AddChild(_saves);
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

    internal WorldSaveResult CreateExampleCopy(string path)
    {
        var result = _workspace.TryCreateExampleCopy(path, out var candidate);
        if (candidate is null)
        {
            RefreshSaves();
            _result.Text = $"无法从示例创建副本：{Describe(result)}";
        }
        else
            Enter(candidate);
        return result;
    }

    internal static string ReadExample(string path)
    {
        using var file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read);
        if (file is null)
            throw new IOException($"{path}: {Godot.FileAccess.GetOpenError()}");
        return file.GetAsText();
    }

    internal WorldSaveResult ImportSave(string path)
    {
        var result = _workspace.Import(path);
        RefreshSaves();
        _result.Text = result.Succeeded
            ? $"已导入本地副本：{result.DisplayName} · {result.SaveId:D}。选择列表中的副本打开。"
            : $"无法导入：{Describe(result)}";
        if (result.Adaptations is { Count: > 0 } adaptations)
            _result.Text += "\n适配提示：" + string.Join("；", adaptations.Select(item => $"{item.Subject}/{item.Relationship}"));
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
        foreach (var child in _examples.GetChildren())
        {
            _examples.RemoveChild(child);
            child.QueueFree();
        }
        foreach (var child in _saves.GetChildren())
        {
            _saves.RemoveChild(child);
            child.QueueFree();
        }
        Entries = _workspace.ListSaves();
        ExampleEntries = _workspace.ListExamples();
        foreach (var entry in ExampleEntries)
        {
            var row = new HBoxContainer();
            _examples.AddChild(row);
            var dependencies = DescribeDependencies(entry.Document?.ExternalDefinitions ?? Array.Empty<WorldSaveDefinitionRef>(), entry.Document?.Source);
            var text = $"{entry.Document?.DisplayName ?? entry.Example.Path}\n{entry.Example.Purpose}\n适用版本 {entry.Example.ApplicableVersion} · 格式 {entry.Document?.FormatVersion.ToString() ?? "未知"}\n依赖：{dependencies}";
            if (!entry.Recovery.Succeeded)
                text += $"\n无法打开：{Describe(entry.Recovery)}";
            else if (entry.Recovery.Adaptations is { Count: > 0 } adaptations)
                text += "\n适配提示：" + string.Join("；", adaptations.Select(item => $"{item.Subject}/{item.Relationship}"));
            row.AddChild(new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = SizeFlags.ExpandFill });
            var button = AddButton(row, "从示例创建副本", () => CreateExampleCopy(entry.Example.Path));
            button.Disabled = !entry.Recovery.Succeeded;
        }
        _result.Text = _workspace.ListFailure is { } failure ? $"存档列表读取失败：{failure.Detail}" : "选择存档打开，或新建空白世界。";
        if (Entries.Count == 0)
            _saves.AddChild(new Label { Text = "暂无本地存档。" });
        foreach (var entry in Entries)
        {
            var row = new HBoxContainer();
            _saves.AddChild(row);
            var saved = entry.LastSavedUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "未知";
            var dependencies = DescribeDependencies(entry.ExternalDefinitions, entry.Source);
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

    internal static string Describe(WorldSaveResult result) =>
        $"{result.Status}" + (result.Diagnostic is { } reason ? $" · {reason.Stage}/{reason.Subject}/{reason.Relationship} · {reason.Detail}" : "");

    private static string DescribeDependencies(IReadOnlyList<WorldSaveDefinitionRef> definitions, WorldSource? source) =>
        string.Join("，", definitions.Select(item => $"{item.DefinitionId} @ {item.SemanticRevision}")
            .Concat(source?.ContentDependencies.Select(item => $"{item.Kind}:{item.Id} @ {item.SemanticRevision}") ?? Array.Empty<string>())
            .Distinct().DefaultIfEmpty("无外部依赖"));

    private static Button AddButton(Container parent, string text, Action action)
    {
        var button = new Button { Text = text };
        button.Pressed += action;
        parent.AddChild(button);
        return button;
    }
}
