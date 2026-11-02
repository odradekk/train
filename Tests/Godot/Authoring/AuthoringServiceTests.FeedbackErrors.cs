using System.Collections.Immutable;
using Train.App;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Scenes.Contracts;
using Train.Scenes.ContractSupport;

namespace Train.Godot.Tests.Authoring;

public sealed partial class AuthoringServiceTests
{
    private static DefinitionDraft.SettlementTemplate FeedbackTemplate() =>
        new(new("template", new("", ""), new("core:town"), null, null, [], [], [], []));

    [TestMethod]
    public void Saving_and_failed_queries_keep_the_last_successful_reference_choices()
    {
        var clock = new FormClock(); using var fixture = new AuthoringFixture(clock: clock); fixture.Open();
        var choices = ImmutableArray.Create(new AuthoringReferenceChoice(AuthoringReferenceKind.SettlementType,
            new ContentReference.Core(new CoreDefinitionReference.SettlementType(new("core:town"))), new("城镇", "")));
        fixture.Host.Factory.QueryAuthoringReferencesCall = (_, _, _) => new ContentResult<ImmutableArray<AuthoringReferenceChoice>>.Success(choices);
        fixture.Scenes.PreviewTemplateCall = (content, _, revision) => new SceneResult<ITemplatePreview>.Success(new FixedTemplatePreview(content.Generation, revision));
        var form = FeedbackTemplate(); fixture.Service.NewDefinition(form);
        fixture.Pump(() => fixture.Service.ReferenceChoices.Length == 1);
        fixture.Service.SaveForm(form); fixture.Pump(() => !fixture.Service.IsBusy);
        Assert.AreEqual(choices, fixture.Service.ReferenceChoices, "保存后新查询完成前应保留成功候选。");
        fixture.Host.Factory.QueryAuthoringReferencesCall = (_, _, _) =>
            new ContentResult<ImmutableArray<AuthoringReferenceChoice>>.Failure([Diagnostic("dependency_missing", "缺少依赖")]);
        clock.Advance(200); fixture.Pump(() => fixture.Service.FormDiagnostics.Any(d => d.Code == "dependency_missing"));
        Assert.AreEqual(choices, fixture.Service.ReferenceChoices, "失败不能删除上一份成功候选。");
    }

    [TestMethod]
    public void Successful_preview_does_not_clear_the_saved_draft_message()
    {
        var clock = new FormClock(); using var fixture = new AuthoringFixture(clock: clock); fixture.Open();
        fixture.Scenes.PreviewTemplateCall = (content, _, revision) => new SceneResult<ITemplatePreview>.Success(new FixedTemplatePreview(content.Generation, revision));
        var form = FeedbackTemplate(); fixture.Service.NewDefinition(form);
        fixture.Pump(() => fixture.Service.TemplatePreview is not null);
        var old = fixture.Service.TemplatePreview;
        fixture.Service.SaveForm(form); fixture.Pump(() => !fixture.Service.IsBusy);
        clock.Advance(200); fixture.Pump(() => fixture.Service.TemplatePreview is not null && !ReferenceEquals(old, fixture.Service.TemplatePreview));
        StringAssert.Contains(fixture.Service.Message!, "包草稿已保存");
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public void Internal_feedback_errors_are_logged_and_not_shown_to_the_user(int fault)
    {
        using var fixture = new AuthoringFixture(); fixture.Open();
        const string detail = "INTERNAL-DETAIL-917";
        fixture.Scenes.PreviewTemplateCall = (content, _, revision) => fault == 2
            ? new SceneResult<ITemplatePreview>.Faulted(new("private_scene_code", SceneStage.Build, null, null, null, null, detail))
            : new SceneResult<ITemplatePreview>.Success(new FixedTemplatePreview(content.Generation, revision));
        if (fault == 0) fixture.Host.Factory.QueryAuthoringReferencesCall = (_, _, _) =>
            new ContentResult<ImmutableArray<AuthoringReferenceChoice>>.Faulted("private_content_code", detail, "private stack");
        if (fault == 1) fixture.Host.Factory.ValidateAuthoringDefinitionCall = (_, _, _, _) =>
            new ContentResult<ImmutableArray<ContentDiagnostic>>.Faulted("private_content_code", detail, "private stack");
        if (fault == 3) fixture.Host.Factory.QueryAuthoringReferencesCall = (_, _, _) => throw new InvalidOperationException(detail);
        fixture.Service.NewDefinition(FeedbackTemplate());
        fixture.Pump(() => fixture.Service.Message != null && fixture.Service.Message != "源包已载入。最终复核到写入结束期间，请确保此包只有一个写入者。");
        Assert.IsTrue(!fixture.Service.Message!.Contains(detail) && !fixture.Service.Message.Contains("private_")
            && fixture.Service.Message.Contains("日志") && fixture.Host.Logs.Any(log => log.Message.Contains(detail)),
            "内部错误必须给出中文提示，并将原始错误写入日志。");
    }

    [TestMethod]
    public void Scene_and_content_diagnostics_use_user_facing_explanations()
    {
        using var fixture = new AuthoringFixture(); fixture.Open();
        fixture.Host.Factory.QueryAuthoringReferencesCall = (_, _, _) =>
            new ContentResult<ImmutableArray<AuthoringReferenceChoice>>.Failure([Diagnostic("private_content_code", "请安装依赖包。")]);
        fixture.Scenes.PreviewTemplateCall = (_, _, _) =>
            new SceneResult<ITemplatePreview>.Failure([new("reference_missing", SceneStage.Build, null, null, null, null, "PRIVATE SCENE DETAIL")]);
        fixture.Service.NewDefinition(FeedbackTemplate());
        fixture.Pump(() => fixture.Service.FormDiagnostics.Length > 0);
        Assert.IsTrue(fixture.Service.Message!.Contains("请安装依赖包。") && fixture.Service.Message.Contains("缺失")
            && !fixture.Service.Message.Contains("private_content_code") && !fixture.Service.Message.Contains("PRIVATE SCENE DETAIL")
            && !fixture.Service.Message.Contains("Build"), "内容只显示诊断消息，场景问题使用面向用户的解释。");
    }

    [TestMethod]
    public void Choosing_an_existing_definition_asset_does_not_create_a_pending_write()
    {
        using var fixture = new AuthoringFixture(); fixture.Open();
        fixture.Service.NewDefinition(new DefinitionDraft.SettlementType(new("town", new("", ""), null, false, null, [], null, null)));
        var directory = Path.Combine(fixture.Core, "resources", "settlement-types", "town");
        Directory.CreateDirectory(directory); var path = Path.Combine(directory, "map.png"); File.WriteAllBytes(path, [9]);
        Assert.AreEqual("map.png", fixture.Service.SelectAsset(path));
        Assert.AreEqual(0, fixture.Service.PendingChanges.Length, "包内自身目录已有资产不应暂存重复字节。");
    }

    [TestMethod]
    public void Asset_read_errors_are_logged_without_exposing_the_path()
    {
        using var fixture = new AuthoringFixture(); fixture.Open();
        fixture.Service.NewDefinition(new DefinitionDraft.SettlementType(new("town", new("", ""), null, false, null, [], null, null)));
        var path = Path.Combine(fixture.Mods, "missing-asset-917.png");
        Assert.IsNull(fixture.Service.SelectAsset(path));
        Assert.IsTrue(!fixture.Service.Message!.Contains("missing-asset-917") && fixture.Service.Message.Contains("日志")
            && fixture.Host.Logs.Any(log => log.Message.Contains("missing-asset-917")), "读取异常原文只写日志。");
    }

    [TestMethod]
    public void New_definition_internal_errors_are_logged_without_exposing_the_detail()
    {
        using var fixture = new AuthoringFixture(); fixture.Open();
        fixture.Host.Factory.PreparePackageWriteCall = (_, _) => new ContentResult<PackageWritePlan>.Faulted("private_code", "PRIVATE ID DETAIL", null);
        var message = fixture.Service.NewDefinitionProblem(new DefinitionDraft.SettlementType(new("town", new("", ""), null, false, null, [], null, null)));
        Assert.IsTrue(message is not null && message.Contains("日志") && !message.Contains("PRIVATE")
            && fixture.Host.Logs.Any(log => log.Message.Contains("PRIVATE ID DETAIL")), "新建校验的内部错误原文只写日志。");
    }
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Preflight_failure_and_fault_keep_the_original_rejection_or_fault(bool faulted)
    {
        using var fixture = new AuthoringFixture();
        fixture.Open(); fixture.SaveForm(); fixture.ConfigureWrite();
        var diagnostic = Diagnostic("preflight-rejected", "候选被拒绝");
        fixture.Host.Factory.PrepareLoadCall = (_, _, _) => faulted
            ? new ContentResult<IContentCandidate>.Faulted("preflight-fault", "原始故障", "original-stack")
            : new ContentResult<IContentCandidate>.Failure([diagnostic]);
        fixture.Service.Publish();
        fixture.Pump(() => !fixture.Service.IsBusy);
        Assert.AreEqual(faulted ? HostOperationStatus.Faulted : HostOperationStatus.Rejected, fixture.LastOperation!.Status);
        if (faulted) Assert.IsTrue(fixture.Service.Message!.Contains("日志") && !fixture.Service.Message.Contains("原始故障")
            && fixture.Host.Logs.Any(log => log.Message.Contains("原始故障")));
        else CollectionAssert.AreEqual(new[] { diagnostic }, fixture.LastOperation.Diagnostics.ToArray());
        Assert.IsFalse(File.Exists(fixture.Output));
        Assert.IsNull(fixture.Service.LastWrite);
    }

    [TestMethod]
    public void Native_open_fault_is_logged_without_exposing_the_exception()
    {
        using var fixture = new AuthoringFixture();
        fixture.Host.Factory.OpenAuthoringPackageCall = (_, _, _, _) => throw new NotImplementedException("native authoring unavailable");
        fixture.Service.OpenCore();
        fixture.Pump(() => !fixture.Service.IsBusy);
        Assert.IsNull(fixture.Service.Package);
        Assert.IsTrue(fixture.Service.Message!.Contains("日志") && !fixture.Service.Message.Contains("native authoring unavailable")
            && fixture.Host.Logs.Any(log => log.Message.Contains("native authoring unavailable")));
    }

    [TestMethod]
    public void Native_write_fault_is_logged_without_exposing_the_exception_or_writing()
    {
        using var fixture = new AuthoringFixture();
        fixture.Open();
        fixture.SaveForm();
        fixture.Host.Factory.PreparePackageWriteCall = (_, _) => throw new NotImplementedException("native package encoder unavailable");
        fixture.Service.Publish();
        fixture.Pump(() => !fixture.Service.IsBusy);
        Assert.IsFalse(File.Exists(fixture.Output));
        Assert.AreSame(fixture.Host.Content, fixture.Coordinator.Content);
        Assert.AreEqual(1, fixture.Service.Drafts().Length);
        Assert.AreEqual(HostOperationStatus.Faulted, fixture.LastOperation!.Status);
        Assert.IsNull(fixture.Service.LastWrite);
        Assert.IsTrue(fixture.Service.Message!.Contains("日志") && !fixture.Service.Message.Contains("native package encoder unavailable")
            && fixture.Host.Logs.Any(log => log.Attributes.TryGetValue("diagnostic_message", out var detail)
                && detail is string text && text.Contains("native package encoder unavailable")));
    }

}
