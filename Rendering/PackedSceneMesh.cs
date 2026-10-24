using System.Collections.Concurrent;
using Godot;
using Train.Scenes.Contracts;

namespace Train.Rendering;

internal sealed record PackedSceneMesh(Vector3[] Positions, Vector3[] Normals, Vector2[] TextureUvs,
    Vector2[] FaceUvs, float[] Custom, int[] Indices)
{
    internal static PackedSceneMesh Create(SceneMesh source, CancellationToken cancellation)
    {
        var positions = new Vector3[source.Vertices.Length];
        var normals = new Vector3[source.Vertices.Length];
        var textureUvs = new Vector2[source.Vertices.Length];
        var faceUvs = new Vector2[source.Vertices.Length];
        var custom = new float[source.Vertices.Length * 4];
        for (var index = 0; index < source.Vertices.Length; index++)
        {
            if ((index & 1023) == 0) cancellation.ThrowIfCancellationRequested();
            var vertex = source.Vertices[index];
            positions[index] = new(vertex.Position.X, vertex.Position.Y, vertex.Position.Z);
            normals[index] = new(vertex.Normal.X, vertex.Normal.Y, vertex.Normal.Z);
            textureUvs[index] = new(vertex.TextureUv.X, vertex.TextureUv.Y);
            faceUvs[index] = new(vertex.FaceUv.X, vertex.FaceUv.Y);
            custom[index * 4] = (int)vertex.ExposedEdges;
            custom[index * 4 + 1] = vertex.RelativeHeight;
            custom[index * 4 + 2] = vertex.Damage;
        }
        var indices = source.TriangleIndices.ToArray();
        // 契约为外侧看 CCW，Godot 为 CW；不反转外向法线。
        for (var index = 0; index < indices.Length; index += 3)
        {
            if ((index & 1023) == 0) cancellation.ThrowIfCancellationRequested();
            (indices[index + 1], indices[index + 2]) = (indices[index + 2], indices[index + 1]);
        }
        return new(positions, normals, textureUvs, faceUvs, custom, indices);
    }
}

internal sealed class SceneMeshPacking : IDisposable
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly ConcurrentDictionary<SceneMesh, PackedSceneMesh> _results = new(ReferenceEqualityComparer.Instance);
    private bool _disposed;
    internal Task Completion { get; }

    internal SceneMeshPacking(SceneMesh[] meshes, Task? previous,
        Func<SceneMesh, CancellationToken, PackedSceneMesh> pack)
    {
        var cancellation = _cancellation.Token;
        void Run()
        {
            try
            {
                foreach (var mesh in meshes)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var result = pack(mesh, cancellation);
                    cancellation.ThrowIfCancellationRequested();
                    _results.TryAdd(mesh, result);
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }
        // 退役作业结束后才启动替代作业，取消期间也不并发打包。
        Completion = previous is null ? Task.Run(Run) : previous.ContinueWith(_ => Run(),
            CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    internal void ThrowIfFailed()
    {
        if (Completion.IsFaulted) Completion.GetAwaiter().GetResult();
    }

    internal bool TryTake(SceneMesh mesh, out PackedSceneMesh? result)
    {
        ThrowIfFailed();
        return _results.TryRemove(mesh, out result);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cancellation.Cancel();
        _results.Clear();
        _ = Completion.ContinueWith(completed =>
        {
            _ = completed.Exception;
            _cancellation.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
