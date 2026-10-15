using Godot;
using Train.App;
using Train.App.Assets;
using Train.Content.Contracts;
using Train.Core.Contracts;
using Train.Rendering;
using Train.Scenes.Contracts;

namespace Train.Ui;

internal partial class GoodsIconCache : Node
{
    private sealed class Entry
    {
        internal Texture2D? Texture;
        internal IDisposable? Lease;
        internal string? Failure;
        internal List<GoodIcon> Controls { get; } = [];
        internal Capture? Pending;
    }
    private sealed class Capture(IDesignPreview preview) : IDisposable
    {
        internal SceneDisplay? Display { get; set; }
        internal SubViewport? Viewport { get; set; }
        internal Godot.Environment? Environment { get; set; }
        internal int CreatedFrame { get; } = Engine.GetFramesDrawn();
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            preview.Dispose();
            Display?.Dispose();
            if (Viewport is { } viewport)
            {
                viewport.GetParent()?.RemoveChild(viewport);
                viewport.Free();
            }
            Environment?.Dispose();
        }
    }
    private readonly Dictionary<(DesignHash, ContentGeneration), Entry> _designs = [];
    private readonly Dictionary<GoodId, Entry> _goods = [];
    private IScenesFactory _factory = null!;
    private GodotMainThreadDispatcher _dispatcher = null!;
    private AssetResourceCache _assets = null!;
    private HostBinding _binding;
    private IContentView? _content;
    private bool _drawn;
    internal event Action<string>? Diagnostic;

    internal void Initialize(IScenesFactory factory, GodotMainThreadDispatcher dispatcher)
    {
        _factory = factory;
        _dispatcher = dispatcher;
        _assets = new(dispatcher);
    }

    internal void Bind(HostBinding binding, IContentView? content)
    {
        _dispatcher.RequireMainThread();
        if (_binding == binding && _content?.Generation == content?.Generation) return;
        Clear();
        _binding = binding;
        _content = content;
    }

    internal GoodIcon Create(GoodId good, long? quantity, int size = 32, bool unavailable = false)
    {
        _dispatcher.RequireMainThread();
        if (size is not (32 or 48 or 64)) throw new ArgumentOutOfRangeException(nameof(size));
        if (!_goods.TryGetValue(good, out var entry))
        {
            entry = Load(good);
            _goods.Add(good, entry);
        }
        var control = new GoodIcon(size) { Quantity = quantity, Unavailable = unavailable };
        entry.Controls.Add(control);
        control.TreeExiting += () => entry.Controls.Remove(control);
        control.SetTexture(entry.Texture, entry.Texture is not null ? null : entry.Failure ?? (entry.Pending is null ? "图标缺失" : "图标正在生成"));
        return control;
    }

    private Entry Load(GoodId good)
    {
        var content = _content;
        if (content is null) return Missing("图标不可用：没有活动内容。 ");
        if (content.FindGood(good) is not ContentLookup<GoodPresentation>.Found found || found.Value.Missing)
            return Missing($"货物定义缺失：{good}。");
        if (found.Value.DisplayDesign is { } design)
        {
            var key = (design, content.Generation);
            if (_designs.TryGetValue(key, out var cached)) return cached;
            var entry = Render(design, content);
            _designs.Add(key, entry);
            return entry;
        }
        if (found.Value.Display.Icon is not { } revision ||
            content.FindAsset(revision) is not ContentLookup<ContentAsset>.Found asset)
            return Missing($"货物图标缺失：{good}。");
        var lease = _assets.AcquireIcon(asset.Value);
        return new() { Texture = lease.Value, Lease = lease };
    }

    private Entry Render(DesignHash design, IContentView content)
    {
        if (content.FindDesign(design) is not ContentLookup<DesignEvaluation>.Found evaluation)
            return Missing($"图标显示设计缺失：{design}。");
        var opened = _factory.PreviewDesign(content, evaluation.Value);
        if (opened is not SceneResult<IDesignPreview>.Success success) return Missing(Describe(opened));
        var capture = new Capture(success.Value);
        var transferred = false;
        try
        {
            var presented = success.Value.Present();
            if (presented is not SceneResult<ISceneFrame>.Success frame)
            {
                return Missing(Describe(presented));
            }
            if (frame.Value.Readiness is SceneReadiness.Loading or SceneReadiness.Failed)
            {
                return Missing("图标只读预览未返回完整可显示帧。");
            }
            foreach (var problem in frame.Value.Diagnostics) Diagnostic?.Invoke($"图标：{problem.Code}：{problem.Detail}");
            var display = capture.Display = new(_assets, content);
            using (var prepared = display.Prepare(frame.Value))
            {
                if (prepared is null) throw new InvalidOperationException("只读图标预览没有返回完整替换帧。");
                prepared.Commit();
            }
            var viewport = capture.Viewport = new SubViewport() { Size = new(128, 128), TransparentBg = true, OwnWorld3D = true,
                RenderTargetUpdateMode = SubViewport.UpdateMode.Once, Msaa3D = Viewport.Msaa.Msaa4X };
            viewport.AddChild(display.Root);
            var environment = capture.Environment = new Godot.Environment() { BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = Colors.Transparent,
                AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = Colors.White,
                AmbientLightEnergy = 0.65f, ReflectedLightSource = Godot.Environment.ReflectionSource.Disabled };
            viewport.AddChild(new WorldEnvironment { Environment = environment });
            viewport.AddChild(new DirectionalLight3D { RotationDegrees = new(-40, -30, 0), LightEnergy = 1.1f });
            AddChild(viewport);
            Aabb? bounds = null;
            void Include(Node node)
            {
                if (node is MeshInstance3D mesh && mesh.Mesh is not null)
                {
                    var box = mesh.GlobalTransform * mesh.GetAabb();
                    bounds = bounds is { } previous ? previous.Merge(box) : box;
                }
                foreach (var child in node.GetChildren()) Include(child);
            }
            Include(display.Root);
            if (bounds is null)
            {
                return Missing("图标预览没有可显示的网格。");
            }
            var center = bounds.Value.GetCenter();
            var radius = Math.Max(bounds.Value.Size.Length() / 2, 0.5f);
            var camera = new Camera3D { Projection = Camera3D.ProjectionType.Orthogonal,
                Size = radius * 2.2f, Near = 0.01f, Far = radius * 8 + 10,
                Position = center + new Vector3(1, 0.8f, 1).Normalized() * (radius * 3 + 1), Current = true };
            viewport.AddChild(camera);
            camera.LookAt(center, Vector3.Up);
            transferred = true;
            return new() { Pending = capture };
        }
        finally
        {
            if (!transferred) capture.Dispose();
        }
    }

    private Entry Missing(string message)
    {
        Diagnostic?.Invoke(message);
        return new() { Failure = message };
    }

    private static string Describe<T>(SceneResult<T> result) where T : notnull => result switch
    {
        SceneResult<T>.Failure failure => string.Join("；", failure.Problems.Select(problem => $"{problem.Code}：{problem.Detail}")),
        SceneResult<T>.Faulted fault => $"{fault.Problem.Code}：{fault.Problem.Detail}",
        _ => throw new ArgumentException("需要失败结果。", nameof(result))
    };

    public override void _Ready() => RenderingServer.FramePostDraw += OnDrawn;
    private void OnDrawn() => _drawn = true;

    public override void _Process(double delta)
    {
        if (!_drawn) return;
        _drawn = false;
        foreach (var entry in _designs.Values)
        {
            if (entry.Pending is not { } capture || Engine.GetFramesDrawn() <= capture.CreatedFrame) continue;
            try
            {
                using var image = capture.Viewport!.GetTexture().GetImage();
                if (image.IsEmpty()) throw new InvalidOperationException("图标离屏渲染返回空图像。");
                entry.Texture = ImageTexture.CreateFromImage(image);
                foreach (var control in entry.Controls) control.SetTexture(entry.Texture);
            }
            finally { capture.Dispose(); entry.Pending = null; }
        }
    }

    private void Clear()
    {
        foreach (var entry in _goods.Values.Concat(_designs.Values).Distinct())
        {
            foreach (var control in entry.Controls) control.SetTexture(null, "图标绑定已失效");
            entry.Controls.Clear();
            entry.Pending?.Dispose();
            if (entry.Lease is { } lease) lease.Dispose();
            else entry.Texture?.Dispose();
        }
        _goods.Clear(); _designs.Clear();
        _drawn = false;
    }

    public override void _ExitTree()
    {
        RenderingServer.FramePostDraw -= OnDrawn;
        Clear();
        _content = null;
        _assets.Dispose();
    }
}
