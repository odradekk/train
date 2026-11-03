using System.Collections.Immutable;
using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.App.Authoring;

internal enum AuthoringWriteStage { None, ValidateDrafts, ValidateCandidate, CheckSource, WriteFiles, ReloadContent }
internal sealed record AuthoringIssue(ContentDiagnostic Diagnostic, AuthoringDefinitionAddress? Definition = null);

internal sealed partial class AuthoringService
{
    private ImmutableArray<AuthoringIssue> sourceIssues = [];
    private ImmutableArray<AuthoringIssue> writeIssues = [];
    private AuthoringIssue? captureIssue;
    private readonly List<PackageEdit> assetEdits = [];
    private readonly Dictionary<AuthoringDefinitionAddress, DraftId> pendingDrafts = [];
    internal ImmutableArray<AuthoringIssue> PublishIssues => writeIssues
        .Where(issue => issue.Diagnostic.Severity != ContentDiagnosticSeverity.Information).ToImmutableArray();
    internal ImmutableArray<PackageEdit> PendingChanges => edits.Values.Concat(assetEdits).ToImmutableArray();
    internal AuthoringDefinitionAddress? DirtyDefinition => formDirty && Form is { } form ? Address(form)
        : editor.IsDirty && editor.Target is DraftTarget.PackageDefinition target && target.Package == Target?.Package
            ? new(target.Kind, target.LocalId) : null;
    internal ImmutableArray<AuthoringIssue> Issues => sourceIssues.Concat(writeIssues).Concat(FormDiagnostics.Select(d => Issue(d, Form is null ? null : Address(Form))))
        .Concat(captureIssue is null ? [] : new[] { captureIssue })
        .Where(issue => issue.Diagnostic.Severity != ContentDiagnosticSeverity.Information).Distinct().ToImmutableArray();
    internal AuthoringWriteStage WriteStage { get; private set; }
    internal bool CanOpen => CanUse && !IsBusy && !host.IsBusy && !editor.IsBusy;
    internal bool HasWorld => host.GetStatus() is not null;
    internal void SetOverlayOpen(bool open) => host.SetOverlayPaused(OverlayPauseSource.Authoring, open);
    internal bool IsPending(AuthoringDefinitionAddress address) => edits.ContainsKey(address);
    internal bool IsDeleted(AuthoringDefinitionAddress address) => edits.GetValueOrDefault(address) is PackageEdit.DeleteDefinition;
    internal bool IsStagedDraft(SavedContentDraft draft) => draft.Target is DraftTarget.PackageDefinition target
        && target.Package == package?.Package && edits.GetValueOrDefault(new(target.Kind, target.LocalId)) is PackageEdit.WriteDefinition
        && pendingDrafts.GetValueOrDefault(new(target.Kind, target.LocalId)) == draft.Id;

    private void AcceptDraft(SavedContentDraft draft)
    {
        if (package is null || draft.Target is not DraftTarget.PackageDefinition target || target.Package != package.Package || target.SourceVersion != package.SourceVersion) return;
        if (draft.Payload is not DraftPayload.Definition definition) throw new ArgumentException("未知包草稿载荷。", nameof(draft));
        var address = Address(definition.Value);
        edits[address] = new PackageEdit.WriteDefinition(definition.Value);
        pendingDrafts[address] = draft.Id;
        Changed?.Invoke();
    }
    internal void CaptureFailed(string message)
    {
        captureIssue = new(new("invalid_form", ContentDiagnosticSeverity.Error, "authoring", null, null, "", null, message),
            Form is { } form ? Address(form) : null);
        Report(message);
    }

    private AuthoringIssue Issue(ContentDiagnostic diagnostic, AuthoringDefinitionAddress? known = null)
    {
        if (known is not null) return new(diagnostic, known);
        (DefinitionKind Kind, string Id)? subject = diagnostic.Subject switch
        {
            ContentReference.Blueprint { Address.Library: BlueprintLibrary.Packages } blueprint => (DefinitionKind.Blueprint, blueprint.Address.Id.Value),
            ContentReference.Core { Definition: CoreDefinitionReference.Voxel voxel } => (DefinitionKind.Voxel, voxel.Id.Value),
            ContentReference.Core { Definition: CoreDefinitionReference.SettlementType type } => (DefinitionKind.SettlementType, type.Id.Value),
            ContentReference.Core { Definition: CoreDefinitionReference.Template template } => (DefinitionKind.SettlementTemplate, template.Id.Value),
            ContentReference.Core { Definition: CoreDefinitionReference.Lair lair } => (DefinitionKind.LairType, lair.Id.Value),
            ContentReference.Core { Definition: CoreDefinitionReference.Composite composite } => (DefinitionKind.Blueprint, composite.Id.Value),
            ContentReference.Core { Definition: CoreDefinitionReference.Creature creature } => (DefinitionKind.Blueprint, creature.Id.Value),
            _ => null
        };
        var prefix = Target?.Package.Value + ":";
        return subject is { } value && value.Id.StartsWith(prefix, StringComparison.Ordinal)
            ? new(diagnostic, new(value.Kind, value.Id[prefix.Length..])) : new(diagnostic);
    }
}
