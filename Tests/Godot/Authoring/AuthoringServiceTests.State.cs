using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Immutable;
using System.Text;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.App.Authoring;
using Train.Core.Contracts;
using Train.Godot.Tests.Host;

namespace Train.Godot.Tests.Authoring;

public sealed partial class AuthoringServiceTests
{
    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public void First_file_failure_is_reported_as_stopped_writing_with_remaining_files()
    {
        using var fixture = new AuthoringFixture();
        fixture.Open(); fixture.SaveForm(); fixture.ConfigureWrite();
        using var locked = new FileStream(Path.Combine(fixture.Core, "source.txt"), FileMode.Open, FileAccess.Read, FileShare.Read);
        fixture.Host.Factory.PreparePackageWriteCall = (basis, _) => new ContentResult<PackageWritePlan>.Success(new(basis.Package, basis.SourceVersion,
            [new("source.txt", ImmutableArray.CreateRange(Encoding.UTF8.GetBytes("blocked"))),
                new("output.txt", ImmutableArray.CreateRange(Encoding.UTF8.GetBytes("written")))], []));
        fixture.Service.Publish(); fixture.Pump(() => !fixture.Service.IsBusy);
        Assert.IsFalse(fixture.Service.LastWrite!.Success);
        Assert.AreEqual(0, fixture.Service.LastWrite.Completed.Length);
        Assert.AreEqual("source.txt", fixture.Service.LastWrite.Failed!.RelativePath);
        Assert.AreEqual("output.txt", fixture.Service.LastWrite.Remaining.Single().RelativePath);
        StringAssert.Contains(fixture.Service.Message!, "写入已停止");
        Assert.IsFalse(fixture.Service.Message!.Contains("一个文件也没写", StringComparison.Ordinal));
        Assert.AreEqual("original", File.ReadAllText(Path.Combine(fixture.Core, "source.txt")));
        Assert.IsFalse(File.Exists(fixture.Output));
        Assert.AreSame(fixture.Host.Content, fixture.Coordinator.Content);
        Assert.IsTrue(File.Exists(fixture.DraftPath(fixture.Service.Drafts().Single().Id)));
    }

    [TestMethod]
    public void Discarding_invalid_form_input_clears_its_capture_problem()
    {
        using var fixture = new AuthoringFixture();
        fixture.Open(); fixture.Service.OpenDefinition(new(DefinitionKind.Blueprint, "blueprint"), false);
        fixture.Service.MarkFormDirty(); fixture.Service.CaptureFailed("无效数字");
        Assert.AreEqual(1, fixture.Service.Issues.Length);
        var closed = false;
        Assert.IsNull(fixture.Editor.RequestClose(() => closed = true));
        fixture.Service.ResolveClose(Train.App.Saves.LeaveChoice.Discard, null);
        Assert.IsTrue(closed);
        Assert.IsNull(fixture.Service.Form);
        Assert.AreEqual(0, fixture.Service.Issues.Length);
    }

    [TestMethod]
    public void Completed_files_show_reload_progress_while_the_reload_is_preparing()
    {
        using var fixture = new AuthoringFixture();
        fixture.Open(); fixture.SaveForm(); fixture.ConfigureWrite();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var load = fixture.Host.Factory.PrepareLoadCall!;
        var calls = 0;
        fixture.Host.Factory.PrepareLoadCall = (request, files, assets) =>
        {
            if (Interlocked.Increment(ref calls) == 2)
            { entered.Set(); HostFixture.WaitForSignal(release, "释放普通重载"); }
            return load(request, files, assets);
        };
        try
        {
            fixture.Service.Publish(); HostFixture.WaitForSignal(entered, "进入普通重载");
            fixture.Service.Process();
            Assert.IsTrue(fixture.Service.IsBusy);
            Assert.AreEqual(AuthoringWriteStage.ReloadContent, fixture.Service.WriteStage);
            Assert.AreSame(fixture.Host.Content, fixture.Coordinator.Content);
        }
        finally { release.Set(); }
        fixture.Pump(() => !fixture.Service.IsBusy);
        Assert.IsTrue(fixture.Service.LastPublishSucceeded);
    }

    [TestMethod]
    public void Pending_changes_follow_saved_edits_and_deletions_instead_of_old_draft_files()
    {
        using var fixture = new AuthoringFixture();
        fixture.Open();
        var address = new AuthoringDefinitionAddress(DefinitionKind.Blueprint, "blueprint");
        fixture.Service.OpenDefinition(address, false);
        fixture.Service.MarkFormDirty();
        Assert.AreEqual(address, fixture.Service.DirtyDefinition);
        Assert.AreEqual(0, fixture.Service.PendingChanges.Length);
        fixture.Service.SaveForm(Blueprint("first save"));
        fixture.Pump(() => !fixture.Service.IsBusy);
        Assert.IsNull(fixture.Service.DirtyDefinition);
        Assert.IsTrue(fixture.Service.IsPending(address));
        Assert.AreEqual(1, fixture.Service.PendingChanges.Length);
        fixture.Service.MarkFormDirty();
        fixture.Service.SaveForm(Blueprint("second save"));
        fixture.Pump(() => !fixture.Service.IsBusy);
        Assert.AreEqual(1, fixture.Service.PendingChanges.Length);
        fixture.Service.DeleteDefinition(address);
        Assert.IsTrue(fixture.Service.IsDeleted(address));
        Assert.IsInstanceOfType<PackageEdit.DeleteDefinition>(fixture.Service.PendingChanges.Single());
        fixture.Open();
        Assert.AreEqual(0, fixture.Service.PendingChanges.Length);
        Assert.IsFalse(fixture.Service.IsPending(address));
        Assert.IsTrue(fixture.Service.Drafts().Length > 0);
    }

    [TestMethod]
    public void Candidate_failure_keeps_the_owning_definition_for_diagnostic_navigation()
    {
        using var fixture = new AuthoringFixture();
        fixture.Open(); fixture.SaveForm();
        var diagnostic = Diagnostic("reference_missing", "引用的定义不存在") with
        { Subject = new ContentReference.Blueprint(new(BlueprintLibrary.Packages, new("core:blueprint"))) };
        var status = new PackageStatus(new(new("core"), "core", "1.0.0", [], []), new("builtin"), PackageState.Invalid, [diagnostic]);
        fixture.ConfigureWrite(report: new([status], [], [], [diagnostic]));
        fixture.Service.Publish(); fixture.Pump(() => !fixture.Service.IsBusy);
        var issue = fixture.Service.Issues.Single();
        Assert.AreEqual(diagnostic, issue.Diagnostic);
        Assert.AreEqual(new AuthoringDefinitionAddress(DefinitionKind.Blueprint, "blueprint"), issue.Definition);
        Assert.IsFalse(File.Exists(fixture.Output));
        Assert.AreEqual(1, fixture.Service.Drafts().Length);
    }

    [TestMethod]
    public void Background_progress_is_observed_and_notified_only_on_the_owner_thread()
    {
        using var fixture = new AuthoringFixture();
        fixture.Open(); fixture.SaveForm(); fixture.ConfigureWrite();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var prepare = fixture.Host.Factory.PreparePackageWriteCall!;
        fixture.Host.Factory.PreparePackageWriteCall = (package, edits) =>
        {
            entered.Set(); HostFixture.WaitForSignal(release, "释放草稿校验");
            return prepare(package, edits);
        };
        var owner = Environment.CurrentManagedThreadId;
        var threads = new List<int>();
        fixture.Service.Changed += () => threads.Add(Environment.CurrentManagedThreadId);
        try
        {
            fixture.Service.Publish(); HostFixture.WaitForSignal(entered, "进入草稿校验");
            fixture.Service.Process();
            Assert.AreEqual(AuthoringWriteStage.ValidateDrafts, fixture.Service.WriteStage);
        }
        finally { release.Set(); }
        fixture.Pump(() => !fixture.Service.IsBusy);
        Assert.IsTrue(fixture.Service.LastWrite?.Success);
        Assert.AreEqual(AuthoringWriteStage.ReloadContent, fixture.Service.WriteStage);
        Assert.IsTrue(threads.Count > 0 && threads.All(thread => thread == owner));
    }
}
