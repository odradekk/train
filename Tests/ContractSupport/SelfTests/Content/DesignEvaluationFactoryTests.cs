using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;

namespace Train.ContractSupport.Tests.Content;

[TestClass]
public sealed class DesignEvaluationFactoryTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Evaluation_preserves_the_requested_root_identity_and_result(bool namedRoot)
    {
        var view = new FixedContentView(HostContentScenario.CreateProjection(new("fixture")));
        var draft = new DesignDraft(new("设备", "测试设备"),
            new DesignSpecification.FunctionalComposite([], null), new([], [], []));
        CompositeDefinitionId? identity = namedRoot ? new("fixture:engine") : null;
        var result = new ContentResult<DesignEvaluation>.Faulted("fixture", "configured result", null);
        IContentFactory factory = new ScriptedContentFactory
        {
            EvaluateDesignCall = (actualView, actualDraft, actualIdentity) =>
            {
                Assert.AreSame(view, actualView);
                Assert.AreSame(draft, actualDraft);
                Assert.AreEqual(identity, actualIdentity);
                return result;
            }
        };

        Assert.AreSame(result, namedRoot ? factory.EvaluateDesign(view, draft, identity) : factory.EvaluateDesign(view, draft));
    }
}
