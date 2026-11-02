using System.Text.Json;
using Godot;
using Train.App.Saves;
using Train.Ui;

namespace Train.App.Measurement;

internal sealed partial class MeasurementRun
{
    internal static (string[] BlockingControls, bool Modal) ReadSceneUi(Node root)
    {
        var size = root.GetViewport().GetVisibleRect().Size;
        var mainWindow = root.GetWindow();
        List<string>? blocking = null;
        var modal = false;
        Visit(root);
        return (blocking?.ToArray() ?? [], modal);

        void Visit(Node node)
        {
            var childCount = node.GetChildCount();
            for (var i = 0; i < childCount; i++)
            {
                var child = node.GetChild(i);
                if (child is Node3D) continue;
                if (child is Control control && control.IsVisibleInTree() &&
                    control.MouseFilter != Control.MouseFilterEnum.Ignore && control.GetGlobalRect().Size.X >= size.X &&
                    control.GetGlobalRect().Size.Y >= size.Y && control is not MacroMap)
                    (blocking ??= []).Add(control.GetPath().ToString());
                if (child is Window window && window != mainWindow && window.Visible && window.Exclusive)
                    modal = true;
                Visit(child);
            }
        }
    }
    // allowRejected 只用于长测的玩家存档往返：被拒绝的读档保留原对局，返回 false 供调用方记录。
    private async Task<bool> LoadSceneInput(bool restoreInput = false, bool allowRejected = false, bool keepPaused = false)
    {
        _scenePhase = "load";
        await Wait(() => !_app.Saves!.IsBusy);
        var menu = _app.GetNode<MainMenu>("MainMenu");
        using var input = File.OpenRead(_input);
        var header = SaveContainer.ReadHeader(input);
        var store = new FileSaveStore(OS.GetUserDataDir());
        Directory.CreateDirectory(store.DirectoryFor(header.Id));
        var copy = store.PathFor(header.Id);
        if (restoreInput || !File.Exists(copy)) File.Copy(_input, copy, overwrite: true);
        menu.OpenSaves();
        var panel = menu.GetChildren().OfType<SavePanel>().Single();
        var list = SceneUiDescendants(panel).OfType<ItemList>().Single();
        var entries = panel.Entries;
        var index = entries.Select((entry, index) => (entry, index)).Single(pair => pair.entry.Path == copy).index;
        list.Select(index);
        var button = SceneUiDescendants(panel).OfType<Button>().Single(item => item.Text == "读取选中存档");
        var previous = _app.Host!.GetStatus()?.Binding;
        _sceneLoadButton = Seconds();
        button.EmitSignal(BaseButton.SignalName.Pressed);
        await Wait(() =>
        {
            if (SceneUiDescendants(panel).OfType<ConfirmationDialog>()
                    .FirstOrDefault(dialog => dialog.Title == "未保存的更改") is { Visible: true } leave &&
                _app.Saves!.Leaving is { Waiting: false })
                leave.EmitSignal(AcceptDialog.SignalName.CustomAction, "discard");
            return !_app.Saves!.IsBusy && _app.Host!.GetStatus() is not null;
        });
        _sceneLoadCompleted = Seconds();
        var replaced = _app.Host!.GetStatus()!.Binding != previous;
        if (!replaced && !allowRejected)
            throw new InvalidOperationException("读档没有替换当前对局：" + _app.Saves!.LastMessage);
        panel.EmitSignal(Window.SignalName.CloseRequested);
        if (!replaced) return false;
        if (_app.Host!.SetSpeed(SimulationSpeed.One) is not null || _app.Host.SetPaused(keepPaused) is not null)
            throw new InvalidOperationException("不能启动测量负载。");
        var previousDraw = _sceneDrawCount;
        await Wait(() => _sceneDrawCount > previousDraw &&
            _app.GetNode<MacroMap>("GameUi/Root/World/MacroMap").IsVisibleInTree() && !_app.WorldScenes!.View.Visible &&
            !panel.Visible && !_app.Saves!.IsBusy);
        _sceneOperationTrace!.WriteLine(JsonSerializer.Serialize(new { kind = "load-map", button = _sceneLoadButton,
            completed = _sceneLoadCompleted, map_drawn = _sceneLastDraw }, SceneJson));
        if (_app.Host!.GetStatus()!.IsReplay) throw new InvalidDataException("测量输入不能是重放。");
        return true;
    }

}
