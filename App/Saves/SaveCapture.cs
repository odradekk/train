using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.App.Saves;

internal sealed record SaveIdentity(Guid Id, string Name);
internal sealed record SaveRevision(CoreRevision Core, ContentRevision Content, string Name);
internal sealed record SaveStatus(HostBinding Binding, SaveIdentity Identity, bool Unsaved, bool Frozen);
internal sealed record SaveFailure(string Code, string Message);
internal sealed record SaveCapture(HostBinding Binding, SaveIdentity Identity, SaveRevision Revision,
    ICoreCapture Core, IContentCapture Content)
{
    internal SaveDocument Encode(SaveIdentity target, DateTimeOffset savedAt) => new(
        new(SaveContainer.FormatVersion, target.Id, target.Name, savedAt, Core.Position, Content.ReferencedPackages),
        Core.Encode(), Content.Encode());
}
