using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.App;
using Train.App.Saves;
using Train.Content.Contracts;
using Train.Content.ContractSupport;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using Train.Godot.Tests.Host;

namespace Train.Godot.Tests.Saves;

public sealed partial class SaveServiceTests
{
    private sealed class Run : IDisposable
    {
        internal string Root { get; } = Directory.CreateTempSubdirectory("train-save-service-").FullName;
        internal HostFixture Host { get; } = new();
        internal FileSaveStore Store { get; }
        internal SaveService Service { get; private set; }
        internal Guid Id { get; }
        internal bool WriterEntered => entered.IsSet;
        internal double Now { get; set; }
        private readonly ManualResetEventSlim entered = new();
        private readonly ManualResetEventSlim release = new();
        private int writeCount;

        internal Run(bool blockFirstWrite = false, bool failPublish = false)
        {
            Host.ReadyHost();
            Host.Session.SetCapture(CapturePurpose.Save, new CoreResult<ICoreCapture>.Success(CoreCapture("core-state")));
            Id = Host.Host.GetSaveStatus()!.Identity.Id;
            Store = new(Root, (path, json) =>
            {
                FileSaveStore.WriteCandidate(path, json);
                if (Interlocked.Increment(ref writeCount) == 1 && blockFirstWrite)
                {
                    entered.Set();
                    HostFixture.WaitForSignal(release, "release file writer");
                }
            }, (candidate, target, overwrite) =>
            {
                if (failPublish) throw new IOException("deterministic publish failure");
                File.Move(candidate, target, overwrite);
            });
            Service = new(Host.Host, Store, Host.CreateEditor(Store), () => Now, () => DateTimeOffset.FromUnixTimeSeconds((long)Now));
            Service.SetAutoSaveInterval(null);
        }

        internal void RecreateService()
        {
            Service.Dispose();
            Service = new(Host.Host, Store, Host.CreateEditor(Store), () => Now, () => DateTimeOffset.FromUnixTimeSeconds((long)Now));
        }

        internal void Process() { Host.Host.ProcessFrame(0); Service.Process(); }
        internal void PumpUntil(Func<bool> complete) => Assert.IsTrue(SpinWait.SpinUntil(() =>
            { Process(); return complete(); }, HostFixture.HangTimeout), "save service did not settle");
        internal void Drain() => PumpUntil(() => !Service.HasPendingWork);
        internal void WaitWriter() => HostFixture.WaitForSignal(entered, "writer started");
        internal void Release() => release.Set();
        internal string Source(string core = "core-state", string content = "content-state")
        {
            var path = Path.Combine(Root, "source.json");
            var document = SaveContainerTests.Document(core) with { ContentPayload = content };
            document = document with { Header = document.Header with { Id = Id } };
            Directory.CreateDirectory(Store.DirectoryFor(Id));
            File.WriteAllText(path, SaveContainer.Encode(document));
            return path;
        }
        internal (ScriptedCoreCandidate Core, ScriptedContentCandidate Content) ConfigureRestore(
            string core = "core-state", string content = "content-state", MissingCoreReference? missing = null,
            ICoreReadView? view = null)
        {
            var session = new ScriptedCoreSession(view ?? new FixedCoreReadView(new(0, 0), HostCoreScenario.Input));
            session.SetCapture(CapturePurpose.Save, new CoreResult<ICoreCapture>.Success(CoreCapture(core)));
            var coreCandidate = new ScriptedCoreCandidate(session, HostCoreScenario.References, missing is null ? [] : [missing]);
            var contentCandidate = new ScriptedContentCandidate(Host.Content);
            Host.Core.DecodeStateCall = value => value == core ? new CoreResult<ICoreCapture>.Success(CoreCapture(value))
                : throw new AssertFailedException("wrong opaque core payload");
            Host.Core.PrepareRestoreCall = (_, _, _) => new CoreResult<ICoreSessionCandidate>.Success(coreCandidate);
            Host.Load = _ => new ContentResult<IContentCandidate>.Success(contentCandidate);
            return (coreCandidate, contentCandidate);
        }
        private static FixedCoreCapture CoreCapture(string payload) => new(new(new(0, 0), "canonical", "hash"),
            HostCoreScenario.References, payload, HostCoreScenario.Input, new(0));
        internal void PublishContent(ulong revision)
        {
            var basis = Host.Host.Content!;
            Host.Load = _ => new ContentResult<IContentCandidate>.Success(new ScriptedContentCandidate(
                new FixedContentView(basis.Core, new(basis.Generation.Value + 1), new(revision)), basis, ContentChange.Persistent));
            Host.Host.BeginReload(CommandSource.Console);
            Host.PumpUntil(() => !Host.Host.IsBusy);
        }
        public void Dispose()
        {
            release.Set();
            try { Drain(); }
            finally
            {
                Service.Dispose();
                Host.Dispose();
                entered.Dispose();
                release.Dispose();
                Directory.Delete(Root, true);
            }
        }
    }
}
