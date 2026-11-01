using Godot;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.Ui;

internal sealed class WorldListPanel
{
    private readonly WorldListKind _kind;
    private readonly LineEdit _search;
    private readonly ItemList _list;
    private readonly List<WorldObjectId> _ids = [];
    internal PanelContainer Panel { get; }

    internal WorldListPanel(PanelContainer panel, VBoxContainer body, WorldListKind kind, Action<WorldObjectId> selected, Action refresh)
    {
        _kind = kind;
        Panel = panel;
        _search = new LineEdit { Name = "Search", PlaceholderText = "搜索名称或编号" };
        body.AddChild(_search);
        _list = new ItemList { Name = "Objects", CustomMinimumSize = new(328, 340),
            AllowReselect = true, MouseForcePassScrollEvents = false };
        body.AddChild(_list);
        body.AddChild(UiStyle.Label("点列表打开详情，不改变当前视角。"));
        _list.ItemSelected += index =>
        {
            if (index >= 0 && index < _ids.Count) selected(_ids[(int)index]);
        };
        _search.TextChanged += _ => refresh();
    }

    internal void Refresh(ICoreReadView view, IContentView content, Func<WorldObjectId, ICoreReadView, IContentView, string> nameOf)
    {
        var items = Items(view).Where(id => nameOf(id, view, content).Contains(_search.Text, StringComparison.OrdinalIgnoreCase)).ToList();
        if (!_ids.SequenceEqual(items))
        {
            _list.Clear(); _ids.Clear(); _ids.AddRange(items);
            foreach (var item in items) _list.AddItem(nameOf(item, view, content));
        }
        else
            for (var i = 0; i < items.Count; i++)
            {
                var name = nameOf(items[i], view, content);
                if (_list.GetItemText(i) != name) _list.SetItemText(i, name);
            }
    }

    private IEnumerable<WorldObjectId> Items(ICoreReadView view) => _kind switch
    {
        WorldListKind.Nodes => view.World.Nodes.Select(item => new WorldObjectId.Node(item.Id)),
        WorldListKind.Edges => view.World.Edges.Select(item => new WorldObjectId.Edge(item.Id)),
        WorldListKind.Combats => view.World.Combats.Select(item => new WorldObjectId.Combat(item.Id)),
        _ => view.World.Trains.Select(item => new WorldObjectId.Train(item.Id))
    };
}
