using Chickensoft.GoDotTest;
using Chickensoft.GodotTestDriver;
using Godot;
using Train.App.Saves;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Ui;
using static Train.Engine.Tests.Assertions;

namespace Train.Engine.Tests;

public partial class SaveTests
{
    private static int[] SaveRows(ItemList list) => Enumerable.Range(0, list.ItemCount)
        .Where(list.IsItemSelectable).ToArray();

    private static int SaveRow(ItemList list, string name) => SaveRows(list).Single(row =>
        list.GetItemText(row).StartsWith(name + " · ", StringComparison.Ordinal));

    [Test]
    public async Task World_groups_sort_and_search_player_titles_and_keyboard_loads_the_selected_file()
    {
        var fixture = new Fixture(TestScene.GetTree());
        var store = new FileSaveStore(OS.GetUserDataDir());
        var owned = new HashSet<Guid>();
        Train.App.App? app = null;
        var view = UiWorldFixture.Save(HostCoreScenario.Input);
        var capture = new FixedCoreCapture(new(view.Position, "groups", "groups-checkpoint"),
            HostCoreScenario.References, "groups core", HostCoreScenario.Input, new(0));
        var reads = 0;
        var core = new ScriptedCoreFactory
        {
            DecodeStateCall = _ =>
            {
                Interlocked.Increment(ref reads);
                return new CoreResult<ICoreCapture>.Success(capture);
            },
            PrepareRestoreCall = (_, _, _) => new CoreResult<ICoreSessionCandidate>.Success(
                new ScriptedCoreCandidate(UiWorldFixture.Session(view), HostCoreScenario.References))
        };
        var content = new ScriptedContentFactory
        {
            PrepareLoadCall = (_, _, _) => new ContentResult<IContentCandidate>.Success(new ScriptedContentCandidate(
                new FixedContentView(HostContentScenario.CreateProjection(HostCoreScenario.Input), new(1))
                { GenerationProfiles = [new(new("fixture:private-profile"), new("群山世界", "测试世界"))] })),
            DecodeCaptureCall = payload => new ContentResult<IContentCapture>.Success(new FixedContentCapture(payload)),
            CaptureReplayCall = (history, _) => new ContentResult<IContentReplayCapture>.Success(
                new FixedContentReplayCapture(history, "groups recording"))
        };
        var time = new DateTimeOffset(2026, 1, 2, 12, 0, 0, TimeSpan.Zero);
        Guid Write(string name, int minute, SaveWorld? world)
        {
            var id = Guid.NewGuid();
            owned.Add(id);
            Require(store.Write(store.PathFor(id), new(new(SaveContainer.FormatVersion, id, name,
                time.AddMinutes(minute), new(0, 0), [], world), "groups core", "groups content"), false).Published,
                "无法准备世界分组存档。");
            return id;
        }
        try
        {
            Write("分组甲旧", 1, new("fixture:private-profile", 71));
            Write("分组乙", 4, new("fixture:private-profile", 82));
            Write("分组甲新", 5, new("fixture:private-profile", 71));
            Write("分组旧格式", 3, null);
            var missing = Write("分组缺失", 2, new("fixture:missing-private", 71));
            app = fixture.LoadScene<Train.App.App>("res://App/App.tscn");
            app.CreateHost = logger => new(core, content, new ScriptedContentFiles(), new UnconfiguredAssetInspector(),
                new(new("builtin"), new("mods"), null, "fixture", []), logger,
                sessionOptions: new(new("fixture", "fixture", "fixture", "fixture", "fixture"), true, 1));
            await fixture.AddToRoot(app);
            await _input.WaitUntil(() => !app.Host!.IsBusy && app.Host.Content is not null);
            await _input.Activate(app.GetNode<MainMenu>("MainMenu").GetNode<Button>("%Saves"));
            var panel = app.GetNode<SavePanel>("GameUi/Root/SavePanel");
            var list = Find<ItemList>(panel, "Saves");
            var search = Find<LineEdit>(panel, "Search");
            await _input.Enter(search, "分组");
            var labels = Enumerable.Range(0, list.ItemCount).Select(list.GetItemText).ToArray();
            Require(labels.Length == 9 && labels[0] == "群山世界 · 种子 71" &&
                labels[1].StartsWith("分组甲新 · ") && labels[2].StartsWith("分组甲旧 · ") &&
                labels[3] == "群山世界 · 种子 82" && labels[4].StartsWith("分组乙 · ") &&
                labels[5] == "未记录世界" && labels[6].StartsWith("分组旧格式 · ") &&
                labels[7] == "未安装的生成配置 · 种子 71" && labels[8].StartsWith("分组缺失 · "),
                "世界必须按配置与种子分组，组按最新保存时间排序，组内按保存时间排序，并显示玩家标题。");
            Require(!string.Join("\n", labels).Contains("fixture:", StringComparison.Ordinal), "列表泄露了生成配置 ID。");
            Require(new[] { 0, 3, 5, 7 }.All(row => !list.IsItemSelectable(row)), "世界标题不能成为可选存档。");
            await _input.WaitUntil(() => app.Saves!.Details is not null);
            var selected = list.GetSelectedItems();
            var details = app.Saves!.Details;
            var before = Volatile.Read(ref reads);
            await _input.DoubleClickItem(list, 3);
            var barrier = app.Host!.BackgroundWork.Enqueue(() => true);
            await _input.WaitUntil(() => barrier.IsCompleted);
            Require(list.GetSelectedItems().SequenceEqual(selected) && ReferenceEquals(details, app.Saves.Details) &&
                Volatile.Read(ref reads) == before && app.Host.GetStatus() is null,
                "点击或双击世界标题不能改选、预读详情或读取存档。");
            await _input.Enter(search, "分组甲旧");
            Require(SaveRows(list).Length == 1 && list.GetItemText(0) == "群山世界 · 种子 71" &&
                list.GetItemText(SaveRow(list, "分组甲旧")).StartsWith("分组甲旧 · "), "名称搜索必须保留匹配存档和所属世界标题。");
            await _input.Enter(search, "群山世界");
            Require(SaveRows(list).Length == 3 && SaveRows(list).Select(list.GetItemText)
                .All(text => text.StartsWith("分组甲") || text.StartsWith("分组乙")), "世界名称搜索必须保留匹配世界的全部存档。");
            await _input.Enter(search, "种子 71");
            Require(SaveRows(list).Length == 3 && SaveRows(list).Select(list.GetItemText)
                .Any(text => text.StartsWith("分组缺失 · ")), "种子搜索必须同时找到已安装和未安装配置的世界。");
            await _input.Enter(search, "未记录世界");
            Require(SaveRows(list).Length == 1 && SaveRow(list, "分组旧格式") >= 0, "未记录世界的存档必须能按组标题搜索。");
            await _input.Enter(search, "分组");
            await _input.ClickItem(list, SaveRow(list, "分组甲旧"));
            await _input.PressKey(Key.Down);
            Require(list.GetSelectedItems().SequenceEqual(new[] { SaveRow(list, "分组乙") }), "向下键必须越过世界标题选择下一组存档。");
            await _input.PressKey(Key.Up);
            Require(list.GetSelectedItems().SequenceEqual(new[] { SaveRow(list, "分组甲旧") }), "向上键必须越过世界标题选择上一组存档。");
            await _input.ClickItem(list, SaveRow(list, "分组缺失"));
            await _input.PressKey(Key.Enter);
            await _input.WaitUntil(() => app.Saves!.Status?.Identity.Id == missing && !app.Saves.HasPendingWork);
            Require(!panel.IsVisibleInTree(), "回车读取必须加载选中文件并关闭存档面板。");
        }
        finally { await Cleanup(fixture, app, owned); }
    }
}
