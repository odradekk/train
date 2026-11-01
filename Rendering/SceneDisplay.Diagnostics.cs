using System.Diagnostics;
using System.Runtime.CompilerServices;
using Train.Scenes.Contracts;

namespace Train.Rendering;

// Temporary opening diagnostics. This branch is never merged into product code.
internal sealed record SceneDisplayInterval(long Call, long Candidate, SceneFrameStamp Frame,
    SceneDisplayWork Work, ulong ProcessFrame, int Thread, SceneObjectId? Object, int? Visual,
    long? Mesh, string Stage, double Started, double Ended);

internal sealed partial class SceneDisplay
{
    internal Action<SceneDisplayInterval>? Diagnose { get; set; }
}

internal static class SceneDisplayDiagnostics
{
    [ThreadStatic] private static Context? _current;
    private static long _nextCall, _nextMesh;
    private sealed record MeshIdentity(long Value);
    private static readonly ConditionalWeakTable<SceneMesh, MeshIdentity> Meshes = new();

    internal sealed class Context(Action<SceneDisplayInterval> emit, long call, long candidate,
        SceneFrameStamp frame, SceneDisplayWork work, ulong processFrame)
    {
        internal readonly Action<SceneDisplayInterval> Emit = emit;
        internal readonly long Call = call;
        internal long Candidate = candidate;
        internal SceneFrameStamp Frame = frame;
        internal readonly SceneDisplayWork Work = work;
        internal readonly ulong ProcessFrame = processFrame;
        internal SceneObjectId? Object;
        internal int? Visual;
        internal long? Mesh;

        internal Context Capture() => new(Emit, Call, Candidate, Frame, Work, ProcessFrame);
    }

    internal static CallScope Call(Action<SceneDisplayInterval>? emit, long candidate,
        SceneFrameStamp frame, SceneDisplayWork work, ulong processFrame) => new(emit is null ? null :
            new Context(emit, Interlocked.Increment(ref _nextCall), candidate, frame, work, processFrame));

    internal readonly struct CallScope : IDisposable
    {
        private readonly bool _initialized;
        private readonly Context? _previous;
        private readonly Span _span;
        internal CallScope(Context? context)
        {
            _initialized = true;
            _previous = _current;
            _current = context;
            _span = Begin(context?.Work + ".Total");
        }
        public void Dispose()
        {
            if (!_initialized) return;
            _span.Dispose(); _current = _previous;
        }
    }

    internal static Span Begin(string stage, SceneObjectId? id = null, int? visual = null,
        SceneMesh? mesh = null) => new(stage, id, visual, mesh);

    internal static bool Enabled => _current is not null;

    internal static void SetFrame(long candidate, SceneFrameStamp frame)
    {
        if (_current is not { } context) return;
        context.Candidate = candidate; context.Frame = frame;
    }

    internal static void SurfaceInterval(string stage, long started, long ended)
    {
        if (_current is { } context)
            Emit(context, stage, started, ended, context.Object, context.Visual, context.Mesh);
    }

    internal static long Start() => _current is null ? 0 : Stopwatch.GetTimestamp();

    internal static void Finish(long started, string stage, SceneMesh? mesh = null)
    {
        if (_current is not { } context || started == 0) return;
        var ended = Stopwatch.GetTimestamp();
        Emit(context, stage, started, ended, context.Object, context.Visual,
            mesh is null ? context.Mesh : MeshId(mesh));
    }

    private static long MeshId(SceneMesh mesh) => Meshes.GetValue(mesh,
        _ => new(Interlocked.Increment(ref _nextMesh))).Value;

    private static void Emit(Context context, string stage, long started, long ended,
        SceneObjectId? id, int? visual, long? mesh) => context.Emit(new(context.Call, context.Candidate,
            context.Frame, context.Work, context.ProcessFrame, Environment.CurrentManagedThreadId,
            id, visual, mesh, stage, started / (double)Stopwatch.Frequency, ended / (double)Stopwatch.Frequency));

    internal readonly struct Span : IDisposable
    {
        private readonly Context? _context;
        private readonly string _stage;
        private readonly long _started;
        private readonly SceneObjectId? _previousObject, _object;
        private readonly int? _previousVisual, _visual;
        private readonly long? _previousMesh, _mesh;

        internal Span(string stage, SceneObjectId? id, int? visual, SceneMesh? mesh)
        {
            _context = _current;
            _stage = stage;
            _previousObject = _context?.Object;
            _previousVisual = _context?.Visual;
            _previousMesh = _context?.Mesh;
            _object = id ?? _previousObject;
            _visual = id is not null ? visual : visual ?? _previousVisual;
            _mesh = _context is null || mesh is null ? (id is not null ? null : _previousMesh) : MeshId(mesh);
            if (_context is not null)
            {
                _context.Object = _object; _context.Visual = _visual; _context.Mesh = _mesh;
            }
            _started = _context is null ? 0 : Stopwatch.GetTimestamp();
        }

        public void Dispose()
        {
            if (_context is null) return;
            var ended = Stopwatch.GetTimestamp();
            _context.Object = _previousObject; _context.Visual = _previousVisual; _context.Mesh = _previousMesh;
            Emit(_context, _stage, _started, ended, _object, _visual, _mesh);
        }
    }

    internal static Func<SceneMesh, CancellationToken, PackedSceneMesh> WrapPacking(
        Func<SceneMesh, CancellationToken, PackedSceneMesh> pack)
    {
        if (_current is not { } current) return pack;
        var captured = current.Capture();
        return (mesh, cancellation) =>
        {
            var previous = _current;
            _current = captured;
            try
            {
                using var interval = Begin("Prepare.BackgroundManagedArrays", mesh: mesh);
                return pack(mesh, cancellation);
            }
            finally { _current = previous; }
        };
    }
}
