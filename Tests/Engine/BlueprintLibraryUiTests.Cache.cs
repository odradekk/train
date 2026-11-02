using System.Collections.Immutable;
using Chickensoft.GoDotTest;
using Godot;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Scenes.Contracts;
using Train.Scenes.ContractSupport;
using Train.Ui;
using static Train.Engine.Tests.Assertions;
using NVector3 = System.Numerics.Vector3;

namespace Train.Engine.Tests;

public partial class BlueprintLibraryUiTests
{
    [Test]
    public async Task Draft_search_filters_and_reopening_reuse_design_previews()
    {
        var drafts = Enumerable.Range(1, 3).Select(number => new SavedContentDraft(new(Guid.NewGuid()), new(1),
            new DraftTarget.LocalBlueprint(null), new DraftPayload.Design(EditorPreviewSession.Draft with
            { Display = new($"缓存车辆 {number}", ""), Specification = new DesignSpecification.Vehicle(SizeClass.Small) }))).ToImmutableArray();
        var evaluations = 0;
        var fixture = await Ready(drafts: drafts, configure: factory => factory.EvaluateDesignCall = (_, draft) =>
        {
            Interlocked.Increment(ref evaluations);
            var complete = (DesignEvaluation.Complete)EditorPreviewSession.Evaluate(draft);
            var hash = new DesignHash(draft.Display.Name);
            return new ContentResult<DesignEvaluation>.Success(complete with { Design = hash, Scene = complete.Scene with { Design = hash } });
        });
        var calls = new List<DesignHash>();
        var previews = new List<LibraryThumbnailPreview>();
        fixture.Factory.PreviewDesignCall = (content, evaluation) =>
        {
            calls.Add(((DesignEvaluation.Complete)evaluation).Design);
            var preview = new LibraryThumbnailPreview(content.Generation); previews.Add(preview);
            return new SceneResult<IDesignPreview>.Success(preview);
        };
        try
        {
            var library = Library(fixture); var input = new UiTestInput(TestScene);
            await input.Activate(Button(library, "草稿", exact: true));
            await FramesUntil(TestScene, () => Volatile.Read(ref evaluations) == 3 && DraftPreviewsReady(library));
            Require(calls.Count == 3 && calls.Distinct().Count() == 3,
                "三份不同设计评估应用到界面后，每份只应打开一次设计预览。");
            var captured = calls.Count;
            await input.Enter(Find<LineEdit>(library, "LibrarySearch"), "缓存");
            await FramesUntil(TestScene, () => DraftPreviewsReady(library));
            Require(Cards(library).Length == 3 && calls.Count == captured,
                "仍匹配三张卡片的搜索必须复用缩略图，不能重新打开设计预览。");
            await input.Activate(Button(library, "车辆", exact: true));
            await FramesUntil(TestScene, () => DraftPreviewsReady(library));
            Require(Cards(library).Length == 3 && calls.Count == captured, "类型筛选重建卡片必须复用设计缩略图。");
            var evaluated = Volatile.Read(ref evaluations);
            await input.Activate(Find<Button>(library, "CloseLibrary"));
            await input.Activate(fixture.App.FindChildren("BlueprintLibrary", "Button", true, false).OfType<Button>().Single());
            await FramesUntil(TestScene, () => Volatile.Read(ref evaluations) >= evaluated + 3 && DraftPreviewsReady(library));
            Require(Cards(library).Length == 3 && calls.Count == captured,
                "关闭后重新开库即使重新评估草稿，也必须按未变设计复用缩略图。");
        }
        finally
        {
            fixture.QueueFree(); await new UiTestInput(TestScene).Frames(3);
            Require(previews.All(preview => preview.Disposals == 1), "界面退出必须释放每份临时设计预览，不能重复释放。");
        }
    }

    private static bool DraftPreviewsReady(Node library) => library.FindChildren("*", "", true, false).OfType<GoodIcon>()
        .Where(icon => icon.IsVisibleInTree()).ToArray() is { Length: 4 } icons &&
        icons.All(icon => icon.TooltipText == "缩略图正在生成" ||
            icon.GetChildren().OfType<TextureRect>().Single().Texture is not null);

    private sealed class LibraryThumbnailPreview(ContentGeneration generation) : IDesignPreview
    {
        public ContentGeneration Content => generation;
        internal int Disposals { get; private set; }
        public SceneResult<ISceneFrame> Present()
        {
            var mesh = new SceneMesh(new SceneMaterial.BuiltIn(BuiltInMaterial.RailSteel),
                [new(new(-1, -1, 0), NVector3.UnitZ, new(0, 0), new(0, 0), ExposedFaceEdges.None, 0, 0),
                 new(new(1, -1, 0), NVector3.UnitZ, new(1, 0), new(1, 0), ExposedFaceEdges.None, 0, 0),
                 new(new(0, 1, 0), NVector3.UnitZ, new(0.5f, 1), new(0.5f, 1), ExposedFaceEdges.None, 1, 0)], [0, 1, 2]);
            var obj = new SceneObject(new(1), null, new(NVector3.Zero, System.Numerics.Quaternion.Identity), null,
                [new SceneVisual.Geometry(mesh, new(false, 0, 0, false))], [], []);
            return new SceneResult<ISceneFrame>.Success(new FixedSceneFrame
            { Update = new(null, new(new(Guid.NewGuid()), new(1), new(1), 1), NVector3.Zero, [obj], [], new([], []), []) });
        }
        public void Dispose() => Disposals++;
    }
}
