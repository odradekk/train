using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Train.Content.Designs;
using Train.Content.Persistence;
using C = Train.Content.Contracts;

namespace Train.Content.Loading;

internal static class ContentEditor
{
    internal static C.ContentResult<C.IContentCandidate> Prepare(ContentView basis, C.ContentEdit edit) => edit switch
    {
        C.ContentEdit.SaveLocalDraft save => SaveDraft(basis, save.Draft),
        C.ContentEdit.DeleteLocalDraft delete => DeleteDraft(basis, delete.Draft),
        C.ContentEdit.SaveBlueprint save => SaveBlueprint(basis, save),
        C.ContentEdit.DeleteBlueprint delete => DeleteBlueprint(basis, delete.Target),
        C.ContentEdit.ConstructionTarget target => Target(basis, target.Target, null),
        C.ContentEdit.InstanceTarget target => Target(basis, target.Target, target),
        _ => throw new ArgumentOutOfRangeException(nameof(edit))
    };

    private static C.ContentResult<C.IContentCandidate> SaveDraft(ContentView basis, C.SavedContentDraft draft)
    {
        if (draft.Target is not (C.DraftTarget.Instance or C.DraftTarget.LocalBlueprint))
            return Failure("invalid_field", "/target", "本局草稿必须以实例或本局蓝图为目标。");
        if (draft.Payload is not C.DraftPayload.Design)
            return Failure("invalid_field", "/payload", "本局草稿必须为设计草稿。");
        string encoded;
        try { encoded = ContentDraftCodec.Encode(draft); }
        catch (JsonException exception) { throw new DefinitionFormatException("/payload", exception.Message, "invalid_field"); }
        if (draft.Target is C.DraftTarget.Instance instance && basis.LocalDrafts.Any(existing =>
            existing.Id != draft.Id && existing.Target is C.DraftTarget.Instance other && other.Id == instance.Id))
            return Failure("duplicate_identity", "/target", "同一实例只能保存一份改装草稿。");
        var previous = basis.LocalDrafts.FirstOrDefault(existing => existing.Id == draft.Id);
        var prepared = new C.PreparedContentEdit.DraftSaved(draft.Id, draft.Revision);
        if (previous is not null && ContentDraftCodec.Encode(previous) == encoded)
            return Candidate(basis, basis, C.ContentChange.None, prepared);
        var drafts = previous is null ? basis.LocalDrafts.Add(draft) : basis.LocalDrafts.Replace(previous, draft);
        return Candidate(basis, basis.WithDrafts(drafts), C.ContentChange.Persistent, prepared);
    }

    private static C.ContentResult<C.IContentCandidate> DeleteDraft(ContentView basis, C.DraftId id)
    {
        var previous = basis.LocalDrafts.FirstOrDefault(draft => draft.Id == id);
        if (previous is null) return Failure("reference_missing", "/draft", "草稿不存在。");
        return Candidate(basis, basis.WithDrafts(basis.LocalDrafts.Remove(previous)), C.ContentChange.Persistent,
            new C.PreparedContentEdit.DraftDeleted(id));
    }

    private static C.ContentResult<C.IContentCandidate> SaveBlueprint(ContentView basis, C.ContentEdit.SaveBlueprint save)
    {
        var address = save.Target switch
        {
            C.BlueprintSaveTarget.New target => new C.BlueprintAddress(target.Library, new(Guid.NewGuid().ToString("D"))),
            C.BlueprintSaveTarget.Overwrite target => target.Address,
            _ => throw new ArgumentOutOfRangeException(nameof(save))
        };
        var rejected = CheckAddress(basis, address);
        if (rejected is not null) return rejected;
        var previous = basis.FindBlueprint(address) as C.ContentLookup<C.BlueprintRecord>.Found;
        if (save.Target is C.BlueprintSaveTarget.Overwrite && !basis.Blueprints.Any(b => b.Address == address))
            return Failure("reference_missing", "/target", "要覆盖的蓝图不存在。");
        var evaluation = PlayerBlueprint.Evaluate(basis, save.Draft);
        if (evaluation is not C.DesignEvaluation.Complete { IsValid: true } complete) return Invalid(evaluation);
        var record = new C.BlueprintRecord(address, complete.Definition, basis.UsedPackages(complete.Definition), complete);
        var encoded = PlayerBlueprint.Encode(record);
        var prepared = new C.PreparedContentEdit.BlueprintSaved(address, complete.Design);
        if (previous is not null && PlayerBlueprint.Encode(previous.Value) == encoded)
            return Candidate(basis, basis, C.ContentChange.None, prepared);
        var view = basis.WithBlueprint(address, record);
        var changes = C.ContentChange.Library;
        if (view.PersistentRevision != basis.PersistentRevision) changes |= C.ContentChange.Persistent;
        ImmutableArray<C.ExternalContentWrite> writes = address.Library == C.BlueprintLibrary.User
            ? [new(address.Id, Encoding.UTF8.GetBytes(encoded).ToImmutableArray())] : [];
        return Candidate(basis, view, changes, prepared, writes);
    }

    private static C.ContentResult<C.IContentCandidate> DeleteBlueprint(ContentView basis, C.BlueprintAddress address)
    {
        var rejected = CheckAddress(basis, address);
        if (rejected is not null) return rejected;
        if (!basis.Blueprints.Any(b => b.Address == address))
            return Failure("reference_missing", "/target", "蓝图不存在。");
        var view = basis.WithBlueprint(address, null);
        var changes = C.ContentChange.Library;
        if (view.PersistentRevision != basis.PersistentRevision) changes |= C.ContentChange.Persistent;
        ImmutableArray<C.ExternalContentWrite> writes = address.Library == C.BlueprintLibrary.User ? [new(address.Id, null)] : [];
        return Candidate(basis, view, changes, new C.PreparedContentEdit.BlueprintDeleted(address), writes);
    }

    private static C.ContentResult<C.IContentCandidate>? CheckAddress(ContentView basis, C.BlueprintAddress address)
    {
        if (address.Library == C.BlueprintLibrary.Packages)
            return Failure("read_only_target", "/target", "包库只读。");
        if (address.Library is not (C.BlueprintLibrary.Local or C.BlueprintLibrary.User) || !PlayerBlueprint.IsId(address.Id.Value))
            return Failure("invalid_field", "/target", "蓝图库或身份无效。");
        if (address.Library == C.BlueprintLibrary.User && basis.Source?.Installation.UserLibrary is null)
            return Failure("invalid_field", "/target", "未配置用户库目录。");
        return null;
    }

    private static C.ContentResult<C.IContentCandidate> Target(ContentView basis, C.DesignDraft draft,
        C.ContentEdit.InstanceTarget? instance)
    {
        var evaluation = PlayerBlueprint.Evaluate(basis, draft);
        if (evaluation is not C.DesignEvaluation.Complete { IsValid: true, Core: not null } complete)
            return Invalid(evaluation);
        C.PreparedContentEdit prepared;
        if (instance is null) prepared = new C.PreparedContentEdit.ConstructionTarget(complete.Core);
        else
        {
            var compared = DesignComparer.Compare(basis, instance.Current, complete);
            if (compared is C.ContentResult<C.DesignChangeEvaluation>.Failure failure)
                return new C.ContentResult<C.IContentCandidate>.Failure(failure.Diagnostics);
            prepared = new C.PreparedContentEdit.InstanceTarget(((C.ContentResult<C.DesignChangeEvaluation>.Success)compared).Value);
        }
        var view = basis.WithTarget(complete);
        return Candidate(basis, view, ReferenceEquals(view, basis) ? C.ContentChange.None :
            C.ContentChange.Presentation | C.ContentChange.Persistent, prepared);
    }

    private static C.ContentResult<C.IContentCandidate> Invalid(C.DesignEvaluation evaluation) =>
        new C.ContentResult<C.IContentCandidate>.Failure(PlayerBlueprint.InvalidDiagnostics(evaluation));

    private static C.ContentResult<C.IContentCandidate> Failure(string code, string field, string message) =>
        new C.ContentResult<C.IContentCandidate>.Failure([PlayerBlueprint.Diagnostic(code, field, message)]);

    private static C.ContentResult<C.IContentCandidate> Candidate(ContentView basis, ContentView view,
        C.ContentChange changes, C.PreparedContentEdit edit, ImmutableArray<C.ExternalContentWrite> writes = default) =>
        new C.ContentResult<C.IContentCandidate>.Success(new ContentCandidate(view,
            new(view.Packages, [], view.AuthoringMissing("edit"), []), basis, changes, edit, writes));
}
