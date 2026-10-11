// 核心捕获情景的有限情景替身检查。
// 未配置操作必须明确失败，避免替身默认成功。
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Train.ContractSupport.Tests.Core;

[TestClass]
[TestCategory("family:unit")]
[TestCategory("scope:core-contract-support")]
public sealed class CaptureScenarioTests
{
    [TestMethod]
    public void FiniteDoubleSatisfiesCaptureContract()
        => CaptureScenario.AssertContract(new CaptureSessionDouble(),
            CaptureSessionDouble.Input, CaptureSessionDouble.InitialReferences,
            CaptureSessionDouble.AdvancedReferences);

    [TestMethod]
    public void FiniteDoubleRejectsUnsupportedInputAndSequence()
    {
        using var session = new CaptureSessionDouble();
        AssertUnconfigured(() => session.Capture(CapturePurpose.Diagnostic));
        AssertUnconfigured(() => session.Advance(2));
        session.Advance(1);
        AssertUnconfigured(() => session.Advance(1));
        AssertUnconfigured(() => session.ReadRecording());
    }

    private static void AssertUnconfigured(Action operation)
    {
        var error = Assert.ThrowsException<InvalidOperationException>(operation);
        StringAssert.StartsWith(error.Message, "UNCONFIGURED:");
    }
}
