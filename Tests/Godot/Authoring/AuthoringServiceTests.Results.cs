using System.Collections.Immutable;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App;
using Train.App.Authoring;
using Train.Content.Contracts;

namespace Train.Godot.Tests.Authoring;

public sealed partial class AuthoringServiceTests
{
    [TestMethod]
    public void External_source_change_refuses_all_writes_and_retains_draft()
    {
        using var fixture = new AuthoringFixture();
        fixture.Open();
        fixture.SaveForm();
        File.WriteAllText(Path.Combine(fixture.Core, "source.txt"), "external");
        fixture.ConfigureWrite();
        fixture.Service.Publish();
        fixture.Pump(() => !fixture.Service.IsBusy);
        Assert.IsFalse(fixture.Service.LastWrite!.Success);
        Assert.AreEqual(0, fixture.Service.LastWrite.Completed.Length);
        Assert.IsFalse(File.Exists(fixture.Output));
        Assert.AreEqual(1, fixture.LoadCalls);
        Assert.AreSame(fixture.Host.Content, fixture.Coordinator.Content);
        Assert.AreEqual(1, fixture.Service.Drafts().Length);
        Assert.AreEqual(AuthoringWriteStage.CheckSource, fixture.Service.WriteStage);
        StringAssert.Contains(fixture.Service.Message!, "一个文件也没写，游戏里的内容保持原样，草稿还在。");
        Assert.AreEqual(HostOperationStatus.Rejected, fixture.LastOperation!.Status);
        Assert.IsNull(fixture.LastOperation.FaultCode);
        var diagnostic = fixture.LastOperation.Diagnostics.Single();
        Assert.AreEqual("stale_basis", diagnostic.Code);
        Assert.AreEqual("", diagnostic.Source!.Path);
        Assert.IsFalse(diagnostic.Message.Contains("未写入任何文件", StringComparison.Ordinal));
        Assert.IsFalse(diagnostic.Message.Contains("草稿保留", StringComparison.Ordinal));
        StringAssert.Contains(diagnostic.Message, "请重新载入");
        StringAssert.Contains(diagnostic.Message, "处理外部修改后重新校验");
    }

    [TestMethod]
    public void Disk_write_survives_reload_rejection_while_old_content_and_draft_remain()
    {
        using var fixture = new AuthoringFixture();
        fixture.Open();
        fixture.SaveForm();
        var savedDraft = fixture.Service.Drafts().Single();
        fixture.ConfigureWrite(rejectReload: true);
        fixture.Service.Publish();
        fixture.Pump(() => !fixture.Service.IsBusy);
        Assert.IsTrue(fixture.Service.LastWrite!.Success);
        Assert.AreEqual("written", File.ReadAllText(fixture.Output));
        Assert.AreSame(fixture.Host.Content, fixture.Coordinator.Content);
        Assert.IsTrue(File.Exists(fixture.DraftPath(savedDraft.Id)));
        Assert.IsNotNull(fixture.Service.Package);
        Assert.AreEqual(AuthoringWriteStage.ReloadContent, fixture.Service.WriteStage);
        StringAssert.Contains(fixture.Service.Message!, "文件已写入，但内容重载失败。游戏里的内容没有切换，草稿还在。");
        Assert.AreEqual("reload rejected", fixture.LastOperation!.Diagnostics.Single().Message);
        Assert.AreEqual(HostOperationStatus.Rejected, fixture.LastOperation!.Status);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public void Partial_write_invalidates_source_and_retains_the_actual_package_draft()
    {
        using var fixture = new AuthoringFixture();
        fixture.Open(); fixture.SaveForm();
        var saved = fixture.Service.Drafts().Single();
        using var locked = new FileStream(Path.Combine(fixture.Core, "source.txt"), FileMode.Open, FileAccess.Read, FileShare.Read);
        fixture.ConfigureWrite();
        fixture.Host.Factory.PreparePackageWriteCall = (basis, _) => new ContentResult<PackageWritePlan>.Success(new(basis.Package, basis.SourceVersion,
            [new("output.txt", ImmutableArray.CreateRange(Encoding.UTF8.GetBytes("written"))), new("source.txt", ImmutableArray.CreateRange(Encoding.UTF8.GetBytes("blocked")))], []));
        fixture.Service.Publish(); fixture.Pump(() => !fixture.Service.IsBusy);
        Assert.IsFalse(fixture.Service.LastWrite!.Success);
        Assert.AreEqual("output.txt", fixture.Service.LastWrite.Completed.Single().RelativePath);
        Assert.IsNotNull(fixture.Service.Package);
        Assert.IsTrue(File.Exists(fixture.DraftPath(saved.Id)));
        Assert.AreSame(fixture.Host.Content, fixture.Coordinator.Content);
        Assert.AreEqual(AuthoringWriteStage.WriteFiles, fixture.Service.WriteStage);
        Assert.AreEqual(HostOperationStatus.Rejected, fixture.LastOperation!.Status);
        var diagnostic = fixture.LastOperation.Diagnostics.Single();
        Assert.AreEqual("package_write_failed", diagnostic.Code);
        Assert.AreEqual("source.txt", diagnostic.Source!.Path);
        StringAssert.Contains(fixture.Service.Message!, "写入已停止，已完成项保留。游戏里的内容没有切换，草稿还在。");
    }
}
