using System.Text.Json;
using Godot;
using Train.App.Saves;
using Train.Ui;

namespace Train.App.Measurement;

internal sealed partial class MeasurementRun
{
    internal static (string[] BlockingControls, bool Modal) ReadSceneUi(Node root)
    {
        var shell = root as GameShell ?? SceneUiDescendants(root).OfType<GameShell>().FirstOrDefault();
        var sceneArea = shell is null ? root.GetViewport().GetVisibleRect() : shell.GetGlobalTransform() * shell.SceneArea;
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
                    control.MouseFilter != Control.MouseFilterEnum.Ignore && control.GetGlobalRect().Encloses(sceneArea) &&
                    control is not MacroMap)
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
        var panel = _app.GetNode<SavePanel>("GameUi/Root/SavePanel");
        var list = SceneUiDescendants(panel).OfType<ItemList>().Single();
        var name = panel.Entries.Single(entry => entry.Path == copy).Header.Name;
        var index = Enumerable.Range(0, list.ItemCount).Single(row => list.IsItemSelectable(row) &&
            list.GetItemText(row).StartsWith(name + " · ", StringComparison.Ordinal) &&
            !list.GetItemText(row).StartsWith(name + " · 自动 · ", StringComparison.Ordinal));
        list.Select(index);
        list.EmitSignal(ItemList.SignalName.ItemSelected, index);
        // 详情和读档共用后台队列，先等详情候选释放，避免将它计入读档耗时。
        await Wait(() => _app.Saves!.Details is not null ||
            _app.Saves.DetailsMessage?.StartsWith("无法读取详情", StringComparison.Ordinal) == true);
        var detailsReady = _app.Saves!.Details is not null;
        var detailsFailure = _app.Saves.DetailsMessage;
        var button = SceneUiDescendants(panel).OfType<Button>().Single(item => item.Text == "读取");
        var previous = _app.Host!.GetStatus()?.Binding;
        _sceneLoadButton = Seconds();
        button.EmitSignal(BaseButton.SignalName.Pressed);
        await Wait(() =>
        {
            if (panel.GetNode<Control>("PromptMask").Visible && _app.Saves!.Leaving is { Waiting: false })
                SceneUiDescendants(panel).OfType<Button>().Single(item => item.Name == "Discard")
                    .EmitSignal(BaseButton.SignalName.Pressed);
            return !_app.Saves!.IsBusy && _app.Host!.GetStatus() is not null;
        });
        _sceneLoadCompleted = Seconds();
        var replaced = _app.Host!.GetStatus()!.Binding != previous;
        if (!replaced && !allowRejected)
            throw new InvalidOperationException("读档没有替换当前对局：" + _app.Saves!.LastMessage);
        panel.Hide();
        if (!replaced) return false;
        if (_app.Host!.SetSpeed(SimulationSpeed.One) is not null || _app.Host.SetPaused(keepPaused) is not null)
            throw new InvalidOperationException("不能启动测量负载。");
        var previousDraw = _sceneDrawCount;
        await Wait(() => _sceneDrawCount > previousDraw &&
            _app.GetNode<MacroMap>("GameUi/Root/World/MacroMap").IsVisibleInTree() && !_app.WorldScenes!.View.Visible &&
            !panel.Visible && !_app.Saves!.IsBusy);
        _sceneOperationTrace!.WriteLine(JsonSerializer.Serialize(new { kind = "load-map", button = _sceneLoadButton,
            completed = _sceneLoadCompleted, map_drawn = _sceneLastDraw,
            details_ready = detailsReady, details_failure = detailsFailure }, SceneJson));
        if (_app.Host!.GetStatus()!.IsReplay) throw new InvalidDataException("测量输入不能是重放。");
        return true;
    }

}
