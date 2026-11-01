using System.Collections.Immutable;
using System.Globalization;
using Godot;
using Train.App;
using Train.App.Editing;
using Train.App.Authoring;
using Train.App.Logging;
using Train.App.Notifications;
using Train.App.Recording;
using Train.App.Saves;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.Ui;

public partial class GameUi
{
    private Button? _createNewGame;
    private Button? _confirmMissing;
    private PanelContainer? _newGame;
    private Label? _newGameError;
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
        body.AddChild(UiStyle.Label("世界种子", true));
        var seed = new LineEdit { Name = "Seed", Text = "0", CustomMinimumSize = new(430, 38) };
        body.AddChild(seed);
        body.AddChild(UiStyle.Label("生成配置", true));
        var profiles = content.GenerationProfiles;
        var profile = new OptionButton { Name = "Profile", CustomMinimumSize = new(430, 38) };
        foreach (var item in profiles) profile.AddItem(item.Display.Name);
        body.AddChild(profile);
        body.AddChild(UiStyle.Label("创建成功后显示暂停的宏观地图。"));
        _newGameError = UiStyle.Label(profiles.IsEmpty ? "没有可用的生成配置。" : "");
        _newGameError.Name = "Problem";
        _newGameError.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _newGameError.AddThemeColorOverride("font_color", UiStyle.Error);
        body.AddChild(_newGameError);
        _confirmMissing = UiStyle.Button("ConfirmMissing", "确认继续", () =>
        {
            var operation = _newOperation;
            if (operation == 0 || _host!.MissingConfirmation?.Operation != operation) return;
            if (_host.ConfirmMissing(operation))
            {
                if (_missingErrorOperation == operation) { _errorPanel.Hide(); _missingErrorOperation = null; }
                return;
            }
            const string message = "缺失确认已失效，请重新创建对局。";
            _logger.Warning("ui", "new_game_confirmation_expired", message);
            ShowError(message);
        }, true);
        _confirmMissing.Hide();
        body.AddChild(_confirmMissing);
        body.AddChild(UiStyle.Button("Cancel", "取消", CancelNewGame));
        var create = _createNewGame = UiStyle.Button("Create", "创建对局", () =>
        {
            if (!ulong.TryParse(seed.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            { _newGameError.Text = "种子必须是 0 至 18446744073709551615 的整数。"; return; }
            if (profile.Selected < 0) return;
            void Start()
            {
            var result = _host.BeginNewGame(profiles[profile.Selected].Id, value, "stage1-world-v1", _host.SessionOptions);
            if (result.Problem is { } problem)
            {
                var message = CommandDiagnostics.LogProblem(_logger, "application", "host_new_game_denied", problem, _host.Content);
                ShowError(message);
                _newGameError.Text = message;
                return;
            }
            _newOperation = result.Operation;
            _confirmMissing!.Hide();
            _newGameError.Text = "正在创建对局，可取消。";
            _createNewGame!.Disabled = true;
            }
            if (_editor.RequestClose(Start) is { } failure) ShowError(failure.Message);
        }, true);
        create.Disabled = profiles.IsEmpty;
        body.AddChild(create);
    }

    private void CancelNewGame()
    {
        if (_newOperation != 0) _host!.Cancel(_newOperation);
        _newOperation = 0;
        _newGame?.Hide();
    }
}
