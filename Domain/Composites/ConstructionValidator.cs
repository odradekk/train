using Train.Content;
using Train.Domain.Voxels;

namespace Train.Domain.Composites;

public static class ConstructionValidator
{
    public static ConstructionStatus Validate(
        CompositeDefinition definition,
        CompositeArchetype archetype,
        VoxelCatalogSnapshot voxels,
        CompositeTransform transform)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(archetype);
        ArgumentNullException.ThrowIfNull(voxels);
        if (!transform.Orientation.IsOrthogonal ||
            !double.IsFinite(transform.OriginX) ||
            !double.IsFinite(transform.OriginY) ||
            !double.IsFinite(transform.OriginZ))
            return ConstructionStatus.NonFiniteTransform;

        var occupancy = new Dictionary<LocalVoxelCoord, CompositeVoxelSpec>();
        foreach (var spec in definition.Voxels)
        {
            if (!occupancy.TryAdd(spec.Cell, spec))
                return ConstructionStatus.DuplicateOccupancy;
            if (!voxels.TryGet(spec.DefinitionId, out var entry))
                return ConstructionStatus.UnknownVoxel;
            if (!spec.Orientation.IsAllowedBy(entry.Definition.AllowedOrientations))
                return ConstructionStatus.DisallowedOrientation;
        }

        var rules = archetype.Construction;
        if (rules.RequireNonEmptySixConnected)
        {
            if (occupancy.Count == 0)
                return ConstructionStatus.Empty;
            if (!VoxelConnectivity.IsSixConnected(occupancy.Keys))
                return ConstructionStatus.Disconnected;
        }

        if (definition.Contacts.Count < rules.ContactAnchorCount)
            return ConstructionStatus.MissingContact;
        if (definition.Contacts.Count > rules.ContactAnchorCount)
            return ConstructionStatus.DuplicateContact;

        if (rules.ContactsMustBeDistinct)
        {
            for (var i = 0; i < definition.Contacts.Count; i++)
            for (var j = i + 1; j < definition.Contacts.Count; j++)
            {
                if (definition.Contacts[i].X == definition.Contacts[j].X &&
                    definition.Contacts[i].Y == definition.Contacts[j].Y &&
                    definition.Contacts[i].Z == definition.Contacts[j].Z)
                    return ConstructionStatus.CoincidentContacts;
            }
        }

        if (rules.ContactsMustBindExistingParts)
        {
            foreach (var contact in definition.Contacts)
            {
                if (!occupancy.ContainsKey(contact.BoundCell))
                    return ConstructionStatus.UnboundContact;
            }
        }

        if (rules.RequireDeclaredSize)
        {
            if (!Enum.IsDefined(definition.SizeClass))
                return ConstructionStatus.UnknownSizeClass;
            if (!VehicleEnvelope.TryFromContacts(definition.SizeClass, definition.Contacts, out var envelope))
                return ConstructionStatus.UnknownSizeClass;
            foreach (var cell in occupancy.Keys)
            {
                if (!VehicleEnvelope.ContainsCell(envelope, cell))
                    return ConstructionStatus.EnvelopeExceeded;
            }
        }

        return ConstructionStatus.Valid;
    }

    public static IdentityRetentionStatus EvaluateIdentity(
        CompositeDefinition definition,
        CompositeArchetype archetype,
        VoxelCatalogSnapshot voxels)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(archetype);
        ArgumentNullException.ThrowIfNull(voxels);
        var tag = archetype.IdentityRetention.RequiredCoreTag;
        foreach (var spec in definition.Voxels)
        {
            if (!voxels.TryGet(spec.DefinitionId, out var entry))
                continue;
            if (entry.Definition.Tags.Contains(tag))
                return IdentityRetentionStatus.Retained;
        }

        return IdentityRetentionStatus.MissingCoreTag;
    }

    public static IdentityRetentionStatus EvaluateIdentity(
        CompositeInstance instance,
        CompositeArchetype archetype,
        VoxelCatalogSnapshot voxels)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(archetype);
        ArgumentNullException.ThrowIfNull(voxels);
        var tag = archetype.IdentityRetention.RequiredCoreTag;
        foreach (var part in instance.Parts)
        {
            if (!voxels.TryGet(part.DefinitionId, out var entry))
                continue;
            if (entry.Definition.Tags.Contains(tag))
                return IdentityRetentionStatus.Retained;
        }

        return IdentityRetentionStatus.MissingCoreTag;
    }
}
