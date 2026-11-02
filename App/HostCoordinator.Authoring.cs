using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.App;

internal sealed partial class HostCoordinator
{
    // Decision note: 2026-10-31-full-screen-authoring-mode
    internal CoreProblem? BeginEditing()
    {
        EnsureOwner();
        if (_active is null)
            return IsBusy ? Problem("busy") : _content is null ? Problem("content_unavailable") : null;
        if (MutationProblem(allowFrozen: true) is { } problem) return problem;
        _active.Driver.SetEditingPaused(true);
        return null;
    }

    internal void EndEditing()
    {
        EnsureOwner();
        if (_active is null) return;
        _active.Driver.SetEditingPaused(false);
        _skipNextFrame = true;
    }

    internal (long Operation, CoreProblem? Problem) BeginPackageWrite(
        Func<ContentLoadRequest, IContentFiles, IAssetInspector, ContentResult<IContentCandidate>> prepare)
    {
        EnsureOwner();
        var problem = _active is null
            ? IsBusy ? Problem("busy") : _content is null ? Problem("content_unavailable") : null
            : MutationProblem();
        return problem is not null ? (0, problem) : BeginReload(CommandSource.UserInterface, prepare);
    }

    internal void EnableAuthoringPackage(PackageId package)
    {
        EnsureOwner();
        if (!_installation.EnabledMods.Contains(package))
            _installation = _installation with { EnabledMods = _installation.EnabledMods.Add(package) };
    }
}
