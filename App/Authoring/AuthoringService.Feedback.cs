using System.Collections.Immutable;
using Train.Content.Contracts;
using Train.App.Editing;
using Train.Core.Contracts;
using Train.Scenes.Contracts;

namespace Train.App.Authoring;

internal sealed partial class AuthoringService
{
    private readonly TimeProvider clock;
    private DefinitionDraft? currentForm;
    private sealed record FeedbackRequest(IAuthoringPackage Package, IContentView Content, DraftId Draft,
        EditRevision Revision, DefinitionDraft Definition, ImmutableArray<PackageEdit> Changes,
        ImmutableArray<PackageEdit> Pending, DateTimeOffset Due);
    private sealed record FeedbackResult(FeedbackRequest Request,
        ContentResult<ImmutableArray<AuthoringReferenceChoice>> Choices,
        ContentResult<ImmutableArray<ContentDiagnostic>> Diagnostics,
        SceneResult<ITemplatePreview>? Preview);
    private FeedbackRequest? pendingFeedback, lastFeedback, runningFeedback, displayedFeedback;
    private Task<FeedbackResult>? feedback;
    internal ImmutableArray<ContentDiagnostic> FormDiagnostics { get; private set; } = [];
    internal ImmutableArray<AuthoringReferenceChoice> ReferenceChoices { get; private set; } = [];

    internal void UpdateForm(DefinitionDraft value)
    {
        if (Form is null || Address(value) != Address(Form)) return;
        currentForm = value; QueueFeedback();
    }

    private void QueueFeedback(bool immediate = false)
    {
        if (package is null || host.Content is not { } content || currentForm is null) return;
        var pending = PendingChanges;
        if (lastFeedback is null || !ReferenceEquals(lastFeedback.Package, package)
            || !ReferenceEquals(lastFeedback.Content, content) || !lastFeedback.Pending.SequenceEqual(pending))
            ReferenceChoices = [];
        FormDiagnostics = [];
        var address = Address(currentForm);
        var changes = pending.Where(edit => edit switch
        {
            PackageEdit.WriteDefinition write => Address(write.Draft) != address,
            PackageEdit.DeleteDefinition delete => delete.Definition != address,
            _ => true
        }).Append(new PackageEdit.WriteDefinition(currentForm)).ToImmutableArray();
        pendingFeedback = lastFeedback = new(package, content, draftId, revision, currentForm, changes, pending,
            clock.GetUtcNow().AddMilliseconds(immediate ? 0 : 200));
        Changed?.Invoke();
    }

    private bool IsCurrent(FeedbackRequest request) => Form is not null && request.Draft == draftId
        && request.Revision == revision && request.Definition == currentForm && ReferenceEquals(request.Package, package)
        && ReferenceEquals(request.Content, host.Content) && request.Pending.SequenceEqual(PendingChanges);

    private void StartFeedback()
    {
        if (feedback is not null || pendingFeedback is not { } request || request.Due > clock.GetUtcNow()) return;
        pendingFeedback = null; runningFeedback = request;
        feedback = Task.Run(() => new FeedbackResult(request,
            host.ContentFactory.QueryAuthoringReferences(request.Package, request.Content, request.Changes),
            host.ContentFactory.ValidateAuthoringDefinition(request.Package, request.Content, request.Changes, Address(request.Definition)),
            request.Definition is DefinitionDraft.SettlementTemplate template
                ? scenes.PreviewTemplate(request.Content, template.Value, request.Revision) : null));
    }

    private void ProcessFeedback()
    {
        if (currentForm is not null && Form is not null && (lastFeedback is null
            || !ReferenceEquals(lastFeedback.Package, package) || !ReferenceEquals(lastFeedback.Content, host.Content)
            || !lastFeedback.Pending.SequenceEqual(PendingChanges))) QueueFeedback();
        if (feedback is { IsCompleted: true } completed)
        {
            feedback = null; var request = runningFeedback; runningFeedback = null;
            try
            {
                var value = completed.GetAwaiter().GetResult();
                if (IsCurrent(value.Request))
                {
                    FormDiagnostics = value.Diagnostics switch
                    {
                        ContentResult<ImmutableArray<ContentDiagnostic>>.Success success => success.Value,
                        ContentResult<ImmutableArray<ContentDiagnostic>>.Failure failure => failure.Diagnostics,
                        _ => []
                    };
                    var messages = new List<string>();
                    if (value.Choices is ContentResult<ImmutableArray<AuthoringReferenceChoice>>.Success choices)
                        ReferenceChoices = choices.Value;
                    else
                    {
                        ReferenceChoices = []; messages.Add(Describe(value.Choices));
                        if (value.Choices is ContentResult<ImmutableArray<AuthoringReferenceChoice>>.Failure failure)
                            FormDiagnostics = FormDiagnostics.Concat(failure.Diagnostics).Distinct().ToImmutableArray();
                    }
                    if (value.Diagnostics is ContentResult<ImmutableArray<ContentDiagnostic>>.Faulted fault) messages.Add(fault.Message);
                    if (value.Preview is SceneResult<ITemplatePreview>.Success preview
                        && preview.Value.Revision == revision && preview.Value.Content == value.Request.Content.Generation)
                    {
                        TemplatePreview = preview.Value; displayedFeedback = value.Request;
                        messages.AddRange(preview.Value.Diagnostics.Select(EditorService.Describe));
                    }
                    else if (value.Preview is not null)
                    { TemplatePreview = null; displayedFeedback = null; messages.Add(value.Preview.ToString() ?? "模板预览失败。"); }
                    if (value.Choices is not ContentResult<ImmutableArray<AuthoringReferenceChoice>>.Success)
                    { TemplatePreview = null; displayedFeedback = null; }
                    if (value.Preview is not null || messages.Count > 0) Report(string.Join("；", messages));
                    else Changed?.Invoke();
                }
            }
            catch (Exception error) { if (request is not null && IsCurrent(request)) Report(error.Message); }
        }
        StartFeedback();
    }

    internal void OpenGrid(DefinitionDraft value)
    {
        if (value is not DefinitionDraft.Blueprint || Form is null || IsBusy) return;
        var original = (DefinitionDraft.Blueprint)Form;
        var address = Address(value);
        if (editor.RequestClose(() =>
        {
            var selected = edits.GetValueOrDefault(address) is PackageEdit.WriteDefinition { Draft: DefinitionDraft.Blueprint saved }
                ? saved : original;
            editor.Open(selected.Value, CreateDraftTarget(selected));
        }) is { } failure) Report(failure.Message);
    }

    internal string? NewDefinitionProblem(DefinitionDraft value)
    {
        if (package is null) return "请先选择源包。";
        if (Definitions.Contains(Address(value))) return "同种类中已有此本地 ID。";
        var result = host.ContentFactory.PreparePackageWrite(package, [new PackageEdit.WriteDefinition(value)]);
        return result switch
        {
            ContentResult<PackageWritePlan>.Failure failure => failure.Diagnostics.FirstOrDefault(d => d.Field == "/id")?.Message,
            ContentResult<PackageWritePlan>.Faulted fault => fault.Message,
            _ => null
        };
    }
}
