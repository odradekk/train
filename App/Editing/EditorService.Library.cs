using System.Collections.Immutable;
using System.Text;
using Train.Content.Contracts;

namespace Train.App.Editing;

internal sealed partial class EditorService
{
    internal void LogDraftEvaluationFailure(Exception error) =>
        _logger.Warning("editor", "editor_draft_evaluation_failed", "草稿评估失败。", exception: error);

    internal ImmutableArray<SavedContentDraft> UserDrafts()
    {
        _userDraftFiles.Clear();
        return ReadDrafts(AuthoringPaths.UserDraftsDirectory(_store.Root), target => target is DraftTarget.UserBlueprint);
    }

    internal ImmutableArray<SavedContentDraft> PackageDrafts(PackageId package) =>
        ReadDrafts(AuthoringPaths.PackageDraftsDirectory(_store.Root, package),
            target => target is DraftTarget.PackageDefinition value && value.Package == package);

    private ImmutableArray<SavedContentDraft> ReadDrafts(string path, Func<DraftTarget, bool> include)
    {
        if (!Directory.Exists(path)) return [];
        var drafts = ImmutableArray.CreateBuilder<SavedContentDraft>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*.json").Order(StringComparer.Ordinal))
            {
                try
                {
                    var result = Host.ContentFactory.DecodeDraft(File.ReadAllText(file, new UTF8Encoding(false, true)));
                    if (result is ContentResult<SavedContentDraft>.Success success)
                    {
                        if (include(success.Value.Target))
                        {
                            drafts.Add(success.Value);
                            if (success.Value.Target is DraftTarget.UserBlueprint) _userDraftFiles[success.Value] = file;
                        }
                    }
                    else DraftReadFailed($"{file}：{LogContentFailure(result)}");
                }
                catch (Exception error) { DraftReadFailed($"{file}：{error.Message}"); }
            }
        }
        catch (Exception error) { DraftReadFailed(error.Message); }
        return drafts.ToImmutable();
    }

    private void DraftReadFailed(string detail)
    {
        _logger.Warning("editor", "editor_draft_read_failed", detail);
        Report("部分草稿无法读取，详情见日志。");
    }

    private readonly Dictionary<SavedContentDraft, string> _userDraftFiles = new(ReferenceEqualityComparer.Instance);
    internal Action<string> DeleteFile { private get; set; } = File.Delete;

    internal void DeleteDraft(SavedContentDraft draft)
    {
        if (IsOpen || IsBusy) { Report("请先结束编辑。"); return; }
        ClearFeedback();
        if (draft.Target is DraftTarget.UserBlueprint)
        {
            if (!_userDraftFiles.TryGetValue(draft, out var path))
            { Report("草稿已更新，请重新选择。"); return; }
            try
            {
                DeleteFile(path);
                _userDraftFiles.Remove(draft);
                Changed?.Invoke();
            }
            catch (Exception error)
            {
                _logger.Warning("editor", "editor_draft_delete_failed", error.ToString());
                Report("草稿丢弃失败，详情见日志。");
            }
            return;
        }
        if (draft.Target is not (DraftTarget.LocalBlueprint or DraftTarget.Instance))
        { Report("该草稿不能从蓝图库丢弃。"); return; }
        var begun = Host.BeginContentEdit(new ContentEdit.DeleteLocalDraft(draft.Id), WriteRequired);
        if (begun.Problem is { } problem) Report(problem);
    }
}
