using System.Collections.Immutable;
using Chickensoft.GoDotTest;
using Godot;
using Train.App.Assets;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Engine.Tests.Assets;
using Train.Rendering;
using Train.Scenes.Contracts;
using Train.Scenes.ContractSupport;
using static Train.Engine.Tests.Assertions;
using NVector3 = System.Numerics.Vector3;
using NQuaternion = System.Numerics.Quaternion;

namespace Train.Engine.Tests;

public partial class SceneDisplayTests
{
    [Test]
    public async Task Packing_keeps_cached_geometry_alive_when_the_other_preparation_is_cancelled()
    {
        using var dispatcher = new GodotMainThreadDispatcher();
        using var cache = new AssetResourceCache(dispatcher);
        List<string> failures = [];
        foreach (var formalDependsOnCandidate in new[] { true, false })
        foreach (var sameKey in formalDependsOnCandidate ? new[] { false, true } : new[] { false })
        {
            using var display = Attach(cache, new FixedContentView());
            var baseline = Frame(1, []);
            await Commit(display, baseline);
            var a = Peak(21);
            var b = Peak(22);
            var c = Peak(23);
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            using var candidateEntered = new ManualResetEventSlim();
            using var candidateRelease = new ManualResetEventSlim();
            display.PackMesh = (mesh, cancellation) =>
            {
                if (ReferenceEquals(mesh, b))
                {
                    entered.Set();
                    Require(release.Wait(TimeSpan.FromSeconds(30)), "缓存租约测试未释放网格打包。");
                }
                if (ReferenceEquals(mesh, c))
                {
                    candidateEntered.Set();
                    Require(candidateRelease.Wait(TimeSpan.FromSeconds(30)), "缓存租约测试未释放候选C打包。");
                }
                return PackedSceneMesh.Create(mesh, cancellation);
            };
            if (formalDependsOnCandidate)
            {
                var candidate = Frame(2, [Object(2, a)]);
                display.ReceiveCandidate(candidate.Update);
                await CompleteCandidate(display);
                display.PrepareCandidate(TimeSpan.FromMilliseconds(4));
                Require(display.PreparedCandidate == candidate.Update.Frame, "候选必须先独占A的网格租约。");
                var formalId = sameKey ? 2UL : 4UL;
                var formal = Frame(3, [Object(3, b), Object(formalId, a)], baseline.Update.Frame);
                Require(display.Prepare(formal) is null, "正式帧必须等待未完成的打包。");
                var packing = display.PreparationPacking;
                try
                {
                    Require(entered.Wait(TimeSpan.FromSeconds(30)), "正式打包必须停在B，尚未消费A。");
                    display.CancelCandidate();
                }
                finally { release.Set(); }
                packing.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
                using var prepared = display.Prepare(formal);
                if (prepared is null) failures.Add($"正式打包依赖的{(sameKey ? "同键" : "不同键")}候选A租约在取消候选后丢失");
                else
                {
                    prepared.Commit();
                    Require(display.Objects.Count == 2 && display.Objects[new(formalId)].Visuals.Length == 1 &&
                        GodotObject.IsInstanceValid(MeshOf(display.Objects[new(formalId)])), "正式帧必须持有有效A资源。");
                }
            }
            else
            {
                var formal = Frame(2, [Object(2, a), Object(3, b)], baseline.Update.Frame);
                Require(display.Prepare(formal) is null, "正式帧必须等待未完成的打包。");
                var formalPacking = display.PreparationPacking;
                Task? candidatePacking = null;
                var candidate = Frame(3, [Object(4, c), Object(5, a)], space: 2);
                try
                {
                    Require(entered.Wait(TimeSpan.FromSeconds(30)), "正式打包必须先发布A并停在B。");
                    Require(display.Prepare(formal) is null, "正式帧必须持有A视觉并等待B。");
                    display.ReceiveCandidate(candidate.Update);
                    display.PrepareCandidate(TimeSpan.FromMilliseconds(4));
                    candidatePacking = display.CandidatePacking;
                    Require(candidateEntered.Wait(TimeSpan.FromSeconds(30)), "候选必须收集缓存A并停在尚未就绪的C。");
                    Require(display.CandidateProgress(candidate.Update.Frame) is (0, 2), "取消正式准备前候选不能消费缓存A。");
                    display.CancelPreparation();
                }
                finally { candidateRelease.Set(); release.Set(); }
                candidatePacking?.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
                formalPacking.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
                display.PrepareCandidate(TimeSpan.FromMilliseconds(4));
                if (display.PreparedCandidate != candidate.Update.Frame)
                    failures.Add("候选打包依赖的正式A租约在取消正式准备后丢失");
                else
                {
                    var promoted = Frame(4, candidate.Update.Upserts, space: 2);
                    using var prepared = display.Prepare(promoted);
                    Require(prepared is not null, "候选持有的资源必须能够晋升。");
                    prepared!.Commit();
                    Require(display.Objects.Count == 2 && GodotObject.IsInstanceValid(MeshOf(display.Objects[new(5)])),
                        "候选晋升后必须持有有效A资源。");
                }
            }
        }
        Require(failures.Count == 0, string.Join("；", failures));
    }

}
