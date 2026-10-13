using System.Collections.Immutable;
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
        new(SaveContainer.FormatVersion, target.Id, target.Name, savedAt,
            new(Core.Position.CompletedSteps, Core.Position.Sequence),
            Content.ReferencedPackages.Select(package => new SavePackage(package.Id.Value, package.Version)).ToImmutableArray()),
        Core.Encode(), Content.Encode());
}
