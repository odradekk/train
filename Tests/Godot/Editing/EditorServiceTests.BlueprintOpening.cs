using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Content.ContractSupport;

namespace Train.Godot.Tests.Editing;

public sealed partial class EditorServiceTests
{
    [TestMethod]
    [DataRow(true, "山地货车")]
    [DataRow(false, "内容缺失或不可用")]
    public void Unavailable_blueprint_shows_name_or_explanation_without_internal_id(bool hasSummary, string expected)
    {
        var address = new BlueprintAddress(BlueprintLibrary.User, new("unavailable-blueprint-internal-id"));
        using var fixture = new EditingFixture(configure: host =>
        {
            var content = new FixedContentView(host.Content.Core, host.Content.Generation)
            {
                Blueprints = hasSummary ? [new(address, new("山地货车", ""), null, false, [])] : [],
                BlueprintRecords = new Dictionary<BlueprintAddress, ContentLookup<BlueprintRecord>>
                {
                    [address] = hasSummary ? new ContentLookup<BlueprintRecord>.Missing([])
                        : new ContentLookup<BlueprintRecord>.NotFound()
                }
            };
            host.Load = _ => new ContentResult<IContentCandidate>.Success(new ScriptedContentCandidate(content));
        });

        fixture.Editor.OpenBlueprint(address);

        Assert.IsFalse(fixture.Editor.IsOpen);
        Assert.IsTrue(fixture.Editor.HasError);
        StringAssert.Contains(fixture.Editor.Message!, "无法打开蓝图");
        StringAssert.Contains(fixture.Editor.Message!, expected);
        Assert.IsFalse(fixture.Editor.Message!.Contains(address.Id.Value, StringComparison.Ordinal));
    }
}
