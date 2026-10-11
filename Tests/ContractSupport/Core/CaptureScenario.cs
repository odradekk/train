// 核心契约的固定捕获情景，断言只依赖公开接口和公开值。
// 有限情景替身不计算模拟、存档编码或引用清单。
using Train.Core.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Train.Core.ContractSupport;

/// <summary>核对捕获跨一步推进及会话关闭后的保留性；调用方准备推进前后引用不同的会话。</summary>
public static class CaptureScenario
{
    /// <summary>依次保存捕获、推进一步、再次捕获并关闭会话；输入身份和两份预期引用由调用方独立提供。</summary>
    public static void AssertContract(ICoreSession session, CoreInputId expectedInput,
        CoreReferenceManifest initialReferences, CoreReferenceManifest advancedReferences)
    {
        Assert.IsFalse(SameReferences(initialReferences, advancedReferences),
            "情景需要推进后引用发生变化，才能验证旧捕获的保留性。");
        var captured = session.Capture(CapturePurpose.Save);
        Assert.IsInstanceOfType<CoreResult<ICoreCapture>.Success>(captured);
        var retained = ((CoreResult<ICoreCapture>.Success)captured).Value;
        var initialPosition = retained.Position;
        var initialRevision = retained.Revision;
        var initialCheckpoint = retained.Checkpoint;
        AssertCapture(retained, initialPosition, expectedInput, initialReferences);
        var payload = retained.Encode();
        Assert.IsFalse(string.IsNullOrEmpty(payload));

        var advanced = session.Advance(1);
        Assert.AreEqual(1u, advanced.CompletedSteps);
        Assert.AreEqual(initialPosition.CompletedSteps + 1, advanced.Position.CompletedSteps);
        Assert.IsNull(advanced.Fault);
        var currentResult = session.Capture(CapturePurpose.Save);
        Assert.IsInstanceOfType<CoreResult<ICoreCapture>.Success>(currentResult);
        var current = ((CoreResult<ICoreCapture>.Success)currentResult).Value;
        AssertCapture(current, advanced.Position, expectedInput, advancedReferences);
        AssertCapture(retained, initialPosition, expectedInput, initialReferences);
        Assert.AreEqual(initialRevision, retained.Revision);
        Assert.AreEqual(initialCheckpoint, retained.Checkpoint);
        Assert.AreEqual(payload, retained.Encode());

        session.Dispose();
        AssertCapture(retained, initialPosition, expectedInput, initialReferences);
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
        Assert.AreEqual(expectedReferences.Templates.Length, capture.References.Templates.Length);
        for (var i = 0; i < expectedReferences.Templates.Length; i++)
        {
            var expected = expectedReferences.Templates[i];
            var actual = capture.References.Templates[i];
            Assert.AreEqual(expected.Owner, actual.Owner);
            Assert.AreEqual(expected.SourceTemplate, actual.SourceTemplate);
            CollectionAssert.AreEqual(expected.Plots.ToArray(), actual.Plots.ToArray());
        }
    }

    private static bool SameReferences(CoreReferenceManifest first, CoreReferenceManifest second)
    {
        if (!first.Designs.SequenceEqual(second.Designs) ||
            !first.Inputs.SequenceEqual(second.Inputs) ||
            !first.Definitions.SequenceEqual(second.Definitions) ||
            first.Templates.Length != second.Templates.Length)
            return false;
        for (var i = 0; i < first.Templates.Length; i++)
        {
            var left = first.Templates[i];
            var right = second.Templates[i];
            if (left.Owner != right.Owner || left.SourceTemplate != right.SourceTemplate ||
                !left.Plots.SequenceEqual(right.Plots))
                return false;
        }
        return true;
    }
}

/// <summary>只支持 Save 捕获及一次 Advance(1)；没有模拟、编码或引用推导规则。</summary>
public sealed class CaptureSessionDouble : ICoreSession
{
    public static CoreInputId Input => new(new string('A', 64));
    public static CoreReferenceManifest InitialReferences => new(
        [new(new string('B', 64))],
        [new(new(1), new("fixture:template"), [new(7, PlotSize.Small)]),
            new(new(2), new("fixture:second-template"), [new(3, PlotSize.Small)])],
        [new(new CoreDefinitionReference.Terrain(new("fixture:terrain")),
            new(new string('C', 64)), "world.nodes[0].terrain", false)],
        [Input]);
    public static CoreReferenceManifest AdvancedReferences => new(
        [new(new string('B', 64))],
        [new(new(1), new("fixture:template"),
                [new(7, PlotSize.Small), new(8, PlotSize.Small)]),
            new(new(2), new("fixture:second-template"), [new(3, PlotSize.Small)])],
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
            advanced ? new(1, 0) : new(0, 0), advanced ? new(2) : new(1),
            advanced ? AdvancedReferences : InitialReferences));
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

    private sealed class FixedCapture(SimulationPosition position, CoreRevision revision,
        CoreReferenceManifest references) : ICoreCapture
    {
        public SimulationPosition Position => position;
        public CoreRevision Revision => revision;
        public CoreInputId InputIdentity => Input;
        public CoreReferenceManifest References { get; } = references;
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
