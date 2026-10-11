// 核心契约的固定捕获情景，断言只依赖公开接口和公开值。
// 有限替身不计算模拟、存档编码或引用清单。
using Train.Core.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Train.ContractSupport.Tests.Core;

/// <summary>固定捕获交接情景；真实实现须通过公开入口准备相同边界后运行断言。</summary>
public static class CaptureScenario
{
    /// <summary>消费固定捕获，不解析编码；推进与关闭不能改变已经交出的值。</summary>
    public static void AssertContract(ICoreSession session, CoreInputId expectedInput,
        CoreReferenceManifest expectedReferences)
    {
        var captured = session.Capture(CapturePurpose.Save);
        Assert.IsInstanceOfType<CoreResult<ICoreCapture>.Success>(captured);
        var retained = ((CoreResult<ICoreCapture>.Success)captured).Value;
        var initialRevision = retained.Revision;
        var initialCheckpoint = retained.Checkpoint;
        AssertCapture(retained, new(0, 0), expectedInput, expectedReferences);
        var payload = retained.Encode();
        Assert.IsFalse(string.IsNullOrEmpty(payload));

        var advanced = session.Advance(1);
        Assert.AreEqual(1u, advanced.CompletedSteps);
        Assert.AreEqual(new SimulationPosition(1, 0), advanced.Position);
        Assert.IsNull(advanced.Fault);
        var currentResult = session.Capture(CapturePurpose.Save);
        Assert.IsInstanceOfType<CoreResult<ICoreCapture>.Success>(currentResult);
        var current = ((CoreResult<ICoreCapture>.Success)currentResult).Value;
        AssertCapture(current, new(1, 0), expectedInput, expectedReferences);
        AssertCapture(retained, new(0, 0), expectedInput, expectedReferences);
        Assert.AreEqual(initialRevision, retained.Revision);
        Assert.AreEqual(initialCheckpoint, retained.Checkpoint);
        Assert.AreEqual(payload, retained.Encode());

        session.Dispose();
        AssertCapture(retained, new(0, 0), expectedInput, expectedReferences);
        Assert.AreEqual(initialRevision, retained.Revision);
        Assert.AreEqual(initialCheckpoint, retained.Checkpoint);
        Assert.AreEqual(payload, retained.Encode());
    }

    private static void AssertCapture(ICoreCapture capture, SimulationPosition position,
        CoreInputId expectedInput, CoreReferenceManifest expectedReferences)
    {
        Assert.AreEqual(position, capture.Position);
        Assert.AreEqual(expectedInput, capture.InputIdentity);
        Assert.AreEqual(position, capture.Checkpoint.Position);
        CollectionAssert.AreEqual(expectedReferences.Designs.ToArray(), capture.References.Designs.ToArray());
        CollectionAssert.AreEqual(expectedReferences.Inputs.ToArray(), capture.References.Inputs.ToArray());
        CollectionAssert.AreEqual(expectedReferences.Definitions.ToArray(), capture.References.Definitions.ToArray());
        Assert.AreEqual(1, capture.References.Templates.Length);
        var template = capture.References.Templates[0];
        Assert.AreEqual(expectedReferences.Templates[0].Owner, template.Owner);
        Assert.AreEqual(expectedReferences.Templates[0].SourceTemplate, template.SourceTemplate);
        CollectionAssert.AreEqual(expectedReferences.Templates[0].Plots.ToArray(), template.Plots.ToArray());
    }
}

/// <summary>只支持 Save 捕获及一次 Advance(1)；没有模拟、编码或引用推导规则。</summary>
public sealed class CaptureSessionDouble : ICoreSession
{
    public static CoreInputId Input => new(new string('A', 64));
    public static CoreReferenceManifest References => new(
        [new(new string('B', 64))],
        [new(new(1), new("fixture:template"), [new(7, PlotSize.Small)])],
        [new(new CoreDefinitionReference.Terrain(new("fixture:terrain")),
            new(new string('C', 64)), "world.nodes[0].terrain", false)],
        [Input]);

    private bool advanced;
    private bool disposed;

    public CoreResult<ICoreCapture> Capture(CapturePurpose purpose)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (purpose != CapturePurpose.Save)
            throw Unconfigured(nameof(Capture));
        return new CoreResult<ICoreCapture>.Success(new FixedCapture(
            advanced ? new(1, 0) : new(0, 0), advanced ? new(2) : new(1)));
    }

    public AdvanceResult Advance(uint steps)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (steps != 1 || advanced)
            throw Unconfigured(nameof(Advance));
        advanced = true;
        return new(1, new(1, 0), [], null, null);
    }

    public ICoreReadView Read() => throw Unconfigured(nameof(Read));
    public SubmissionResult Submit(CoreCommand command, CommandSource source) => throw Unconfigured(nameof(Submit));
    public CoreRecordingWindow ReadRecording() => throw Unconfigured(nameof(ReadRecording));
    public CoreResult<RecordingStart> CaptureRecordingStart() => throw Unconfigured(nameof(CaptureRecordingStart));
    public RecordingMaintenance ConfirmRecordingStart(RecordingPointId point, RecordingWriteOutcome outcome)
        => throw Unconfigured(nameof(ConfirmRecordingStart));
    public void Dispose() => disposed = true;

    private static InvalidOperationException Unconfigured(string operation)
        => new($"UNCONFIGURED: capture scenario does not support {operation} input or sequence.");

    private sealed class FixedCapture(SimulationPosition position, CoreRevision revision) : ICoreCapture
    {
        public SimulationPosition Position => position;
        public CoreRevision Revision => revision;
        public CoreInputId InputIdentity => Input;
        public CoreReferenceManifest References { get; } = CaptureSessionDouble.References;
        public CoreCheckpoint Checkpoint { get; } = CreateCheckpoint(position, revision);
        public string Encode() => $"opaque fixture capture {position.CompletedSteps}:{revision.Value}";

        private static CoreCheckpoint CreateCheckpoint(SimulationPosition position, CoreRevision revision)
        {
            var text = $"fixed checkpoint {position.CompletedSteps}:{revision.Value}";
            var digest = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text));
            return new(position, text, Convert.ToHexString(digest));
        }
    }
}
