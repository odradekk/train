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
        if (!transform.IsFinite)
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
        return EvaluateIdentity(definition.Voxels.Select(spec => spec.DefinitionId), archetype, voxels);
    }

    public static IdentityRetentionStatus EvaluateIdentity(
        CompositeInstance instance,
        CompositeArchetype archetype,
        VoxelCatalogSnapshot voxels)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return EvaluateIdentity(instance.Parts.Select(part => part.DefinitionId), archetype, voxels);
    }

    public static IdentityRetentionStatus EvaluateIdentity(
        IEnumerable<VoxelDefinitionId> definitionIds,
        CompositeArchetype archetype,
        VoxelCatalogSnapshot voxels)
    {
        ArgumentNullException.ThrowIfNull(definitionIds);
        ArgumentNullException.ThrowIfNull(archetype);
        ArgumentNullException.ThrowIfNull(voxels);
        var tag = archetype.IdentityRetention.RequiredCoreTag;
        foreach (var definitionId in definitionIds)
        {
            if (!voxels.TryGet(definitionId, out var entry))
                continue;
            if (entry.Definition.Tags.Contains(tag))
                return IdentityRetentionStatus.Retained;
        }

        return IdentityRetentionStatus.MissingCoreTag;
    }
}
