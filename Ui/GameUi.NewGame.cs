using System.Globalization;
using Godot;
using Train.App;
using Train.App.Saves;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.Ui;

public partial class GameUi
{
    // Decision note: 2026-10-31-menus-saves-and-settings
    private Button? _createNewGame;
    private PanelContainer? _newGame;
    private Label? _newGameError;
    private ProgressBar? _newGameProgress;
    private long _newOperation;
    private long _reportedMissing;

    private void OpenNewGame()
    {
        if (_host?.Content is not { } content || _host.SessionOptions is null) return;
        if (_newGame is not null)
        {
            _root.RemoveChild(_newGame); _newGame.QueueFree();
        }
        _newGame = UiStyle.Panel(_root, "NewGame", "新游戏", new(720, 260), 480,
            CancelNewGame, out var body, centered: true);
        _newGame.GetNode<ScrollContainer>("Layout/Content").FollowFocus = true;
        body.AddChild(UiStyle.Label("世界生成配置", true));
        var profiles = content.GenerationProfiles;
        var profile = new OptionButton { Name = "Profile" };
        foreach (var item in profiles) profile.AddItem(item.Display.Name);
        body.AddChild(profile);
        var description = UiStyle.Label(profiles.IsEmpty ? "" : profiles[0].Display.Description);
        description.Name = "ProfileDescription";
        description.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        body.AddChild(description);
        profile.ItemSelected += index => description.Text = profiles[(int)index].Display.Description;
        body.AddChild(UiStyle.Label("世界种子", true));
        var seedRow = new HBoxContainer();
        var seed = new LineEdit { Name = "Seed", Text = RandomSeed(), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        seedRow.AddChild(seed);
        seedRow.AddChild(UiStyle.Button("RandomSeed", "随机", () => seed.Text = RandomSeed()));
        body.AddChild(seedRow);
        body.AddChild(UiStyle.Label("存档名称", true));
        var name = new LineEdit { Name = "SaveName", Text = "新的对局" };
        body.AddChild(name);
        _newGameError = UiStyle.Label(profiles.IsEmpty ? "没有可用的生成配置。" : "");
        _newGameError.Name = "Problem";
        _newGameError.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        body.AddChild(_newGameError);
        _newGameProgress = new ProgressBar { Name = "GenerationProgress", Indeterminate = true,
            ShowPercentage = false, Visible = false, CustomMinimumSize = new(0, 10) };
        body.AddChild(_newGameProgress);
        var hint = UiStyle.Label("新对局开始时游戏是暂停的，按空格开始。");
        hint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        body.AddChild(hint);
        var actions = new HBoxContainer { Name = "Actions", Alignment = BoxContainer.AlignmentMode.End };
        _newGame.GetNode<VBoxContainer>("Layout").AddChild(actions);
        actions.AddChild(UiStyle.Button("Cancel", "取消", CancelNewGame));
        _createNewGame = UiStyle.Button("Create", "开始游戏", () =>
        {
            if (_newOperation != 0)
            {
                if (_host.MissingConfirmation?.Operation == _newOperation) _host.ConfirmMissing(_newOperation);
                return;
            }
            var saveName = name.Text.Trim();
            if (saveName.Length == 0) { NewGameProblem("存档名称不能为空。"); return; }
            if (!ulong.TryParse(seed.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            { NewGameProblem("种子必须是 0 至 18446744073709551615 的整数。"); return; }
            if (profile.Selected < 0) return;
            void Start()
            {
                var result = _host.BeginNewGame(profiles[profile.Selected].Id, value, "stage1-world-v1",
                    _host.SessionOptions, new SaveIdentity(Guid.NewGuid(), saveName));
                if (result.Problem is { } problem)
                {
                    var message = CommandDiagnostics.LogProblem(_logger, "application", "host_new_game_denied", problem, _host.Content);
                    NewGameProblem(message);
                    return;
                }
                _newOperation = result.Operation;
                SetNewGameBusy(true);
                _newGameError!.Text = "正在准备内容和生成世界，可以取消。";
                _newGameError.AddThemeColorOverride("font_color", UiStyle.Foreground);
            }
            if (_editor.RequestClose(Start) is { } failure) NewGameProblem(failure.Message);
        }, true);
        _createNewGame.Disabled = profiles.IsEmpty;
        actions.AddChild(_createNewGame);
        if (!profiles.IsEmpty) profile.GrabFocus();
    }

    private static string RandomSeed()
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        Random.Shared.NextBytes(bytes);
        return BitConverter.ToUInt64(bytes).ToString(CultureInfo.InvariantCulture);
    }

    private void NewGameProblem(string message)
    {
        _newGameError!.Text = message;
        _newGameError.AddThemeColorOverride("font_color", UiStyle.Error);
    }

    private void SetNewGameBusy(bool busy)
    {
        foreach (var input in _newGame!.FindChildren("*", "LineEdit", true, false).Cast<LineEdit>()) input.Editable = !busy;
        foreach (var input in _newGame.FindChildren("*", "OptionButton", true, false).Cast<OptionButton>()) input.Disabled = busy;
        ((Button)_newGame.FindChild("RandomSeed", true, false)).Disabled = busy;
        _createNewGame!.Disabled = busy;
        _newGameProgress!.Visible = busy;
    }

    private void ShowNewGameMissing(ContentPreparationReport content, System.Collections.Immutable.ImmutableArray<MissingCoreReference> core)
    {
        var packages = core.Select(item => ReferencePackage(item.Reference))
            .Where(id => id is not null).Select(id => id!.Value).Distinct()
            .Select(id => content.Packages.FirstOrDefault(package => package.Metadata?.Id == id)?.Metadata?.Name ?? id.Value);
        _newGameError!.Text = "缺失内容的来源内容包：" + string.Join("、", packages) + "。\n"
            + "继续后，缺失内容保留为占位，相关功能失效；缺失货物不能使用或交易，缺失的格不能改装或修理。装回内容包后可恢复。";
        _newGameError.AddThemeColorOverride("font_color", UiStyle.Warning);
        _newGameProgress!.Hide();
        _createNewGame!.Text = "仍然开始";
        _createNewGame.Disabled = false;
        _createNewGame.GrabFocus();
        var details = core.Select(item => $"{item.Reference} · {item.FieldPath}");
        _logger.Warning("ui", "new_game_missing_confirmation", string.Join("\n", details));
    }

    private static PackageId? ReferencePackage(CoreDefinitionReference reference)
    {
        var id = reference switch
        {
            CoreDefinitionReference.Voxel item => item.Id.Value,
            CoreDefinitionReference.Composite item => item.Id.Value,
            CoreDefinitionReference.Process { Id: ProcessRef.Production item } => item.Definition.Value,
            CoreDefinitionReference.Process { Id: ProcessRef.Recovery item } => item.Definition.Value,
            CoreDefinitionReference.Rule item => item.Id.Value,
            CoreDefinitionReference.SettlementType item => item.Id.Value,
            CoreDefinitionReference.Template item => item.Id.Value,
            CoreDefinitionReference.Terrain item => item.Id.Value,
            CoreDefinitionReference.Creature item => item.Id.Value,
            CoreDefinitionReference.Lair item => item.Id.Value,
            _ => null
        };
        return id?.IndexOf(':') is > 0 and var separator ? new(id[..separator]) : null;
    }

    private void CancelNewGame()
    {
        if (_newOperation != 0) _host!.Cancel(_newOperation);
        _newOperation = 0;
        _newGame?.Hide();
    }
}
