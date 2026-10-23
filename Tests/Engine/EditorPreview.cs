using Godot;
using Train.App;
using Train.App.Editing;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Scenes.Contracts;
using Train.Scenes.ContractSupport;
using Train.Ui;

namespace Train.Engine.Tests;

// 有限契约替身只提供界面与适配层所需资料，不实现占格或设计合法性规则。
public partial class EditorPreview : Node
{
    internal Train.App.App App { get; private set; } = null!;
    internal EditorScenes Factory { get; } = new()
    {
        ConfigureSession = session => session.SetState(state => state with
        {
            Elements =
            [
                new(new(1), null, EditElementKind.Cell, new(0, 0, 0), null, [], false),
                new(new(10), null, EditElementKind.Assembly, new(8, 0, 0), null, [0], false),
                new(new(20), null, EditElementKind.Assembly, new(0, 0, 0), null, [], true),
                new(new(30), null, EditElementKind.FunctionalComposite, new(0, 0, 0), null, [], false)
            ]
        })
    };
    internal EditorSceneController Controller => App.FindChildren("DesignEditor", "", true, false).OfType<DesignEditorPanel>().Single().Scene;
    internal EditorService Editor => App.Editor!;
    internal FixedContentView? Content { get; init; }
    internal FixedCoreReadView? World { get; init; }
    internal GridBounds? OccupiedBounds { get; init; }
    private bool _opened;
    public static void Start(Node root) => root.AddChild(new EditorPreview { Name = "EditorPreview" });
    public override void _Ready()
    {
        App = GD.Load<PackedScene>("res://App/App.tscn").Instantiate<Train.App.App>();
        App.CreateHost = logger =>
        {
            var content = Content ?? new FixedContentView(HostContentScenario.CreateProjection(HostCoreScenario.Input), new(1))
            { Blueprints = [], LocalDrafts = [], Voxels = [], GenerationProfiles = [] };
            var host = WorldSceneFixture.CreateHost(new ScriptedCoreSession(World ?? WorldSceneFixture.EmptyWorld(new(0, 0))), content, logger);
            ((ScriptedContentFactory)host.ContentFactory).EvaluateDesignCall = (_, draft) => new ContentResult<DesignEvaluation>.Success(EditorPreviewSession.Evaluate(draft, OccupiedBounds));
            return host;
        };
        App.CreateScenes = () => Factory;
        AddChild(App); App.Saves!.SetAutoSaveInterval(null); App.Host!.AutoPauseOnFocusLoss = false;
    }
    public override void _Process(double delta)
    {
        if (_opened || App.Host?.GetStatus() is null) return;
        _opened = true; App.Editor!.Open(EditorPreviewSession.Draft, new DraftTarget.LocalBlueprint(null));
    }
}

internal static class EditorPreviewSession
{
    private static DesignDraft Child { get; } = new(new("嵌套设备", ""), new DesignSpecification.Assembly(null), new([], [], []));
    internal static DesignDraft Draft { get; } = new(new("编辑器契约预览", "有限场景替身，验证真实 Godot 界面与渲染适配。"), new DesignSpecification.Assembly(null), new([], [], [new NestedDesignItem.AssemblyCopy(new(8, 0, 0), VoxelOrientation.PzPy, Child)]));
    internal static DesignEvaluation Evaluate(DesignDraft draft, GridBounds? occupied = null) => new DesignEvaluation.Complete(new("fixture-design"), new("fixture-composition"), draft, null,
        new(null, occupied ?? new(new(0, 0, 0), new(1, 1, 1)), null, true, null, null, [], [], [], [], []), true, []);
}
