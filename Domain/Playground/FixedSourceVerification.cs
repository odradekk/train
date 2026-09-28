using System.Globalization;
using Train.Content;
using Train.Domain.Composites;
using Train.Domain.Voxels;

namespace Train.Domain.Playground;

internal readonly record struct FixedSourceCheck(
    bool Succeeded,
    WorldSaveStatus Status,
    WorldSaveDiagnostic? Diagnostic,
    IReadOnlyList<WorldSaveAdaptation> Adaptations)
{
    public static FixedSourceCheck Pass(IReadOnlyList<WorldSaveAdaptation>? adaptations = null) =>
        new(true, WorldSaveStatus.Loaded, null, adaptations ?? Array.Empty<WorldSaveAdaptation>());

    public static FixedSourceCheck Fail(WorldSaveStatus status, WorldSaveDiagnostic diagnostic) =>
        new(false, status, diagnostic, Array.Empty<WorldSaveAdaptation>());
}

/// <summary>
/// Rebuilds a saved fixed configuration and compares it with <see cref="WorldSource.BaseFingerprint"/>.
/// A fixed source without that fingerprint is an unsupported document and does not load.
/// </summary>
internal static class FixedSourceVerification
{
    public static FixedSourceCheck Check(
        WorldSaveDocument document,
        VoxelCatalog catalog,
        CompositeCatalog composites)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(composites);
        if (document.Source is not { } source)
            return FixedSourceCheck.Pass();
        if (source.BaseFingerprint.Length == 0)
        {
            return FixedSourceCheck.Fail(
                WorldSaveStatus.IncompatibleBase,
                new WorldSaveDiagnostic(
                    "base",
                    source.ConfigurationId,
                    "base_fingerprint",
                    "a fixed-configuration save without a base fingerprint is not supported"));
        }

        var resolved = PlaygroundConfigurations.TryResolve(source.ConfigurationId, catalog, composites, out var live);
        if (resolved != PlaygroundEstablishStatus.Established || live is null)
            return FixedSourceCheck.Fail(Map(resolved), DiagnoseResolve(source.ConfigurationId, resolved));
        if (live.ConfigurationVersion != source.ConfigurationVersion)
        {
            return FixedSourceCheck.Fail(
                WorldSaveStatus.IncompatibleBase,
                new WorldSaveDiagnostic("base", source.ConfigurationId, "configuration_version", "the saved configuration version is not the version this build declares"));
        }

        if (!SameParameters(source.EffectiveParameters, live.EffectiveParameters))
        {
            return FixedSourceCheck.Fail(
                WorldSaveStatus.IncompatibleBase,
                new WorldSaveDiagnostic("base", source.ConfigurationId, "effective_parameters", "the saved effective parameters do not match the current configuration"));
        }

        if (!string.Equals(source.Parameter("base.seed"), document.Seed.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
        {
            return FixedSourceCheck.Fail(
                WorldSaveStatus.IncompatibleBase,
                new WorldSaveDiagnostic("base", source.ConfigurationId, "seed", "the document seed does not match the saved configuration"));
        }

        if (!string.Equals(source.Parameter("base.generator_version"), document.GeneratorVersion, StringComparison.Ordinal))
        {
            return FixedSourceCheck.Fail(
                WorldSaveStatus.IncompatibleBase,
                new WorldSaveDiagnostic("base", source.ConfigurationId, "generator_version", "the document generator version does not match the saved configuration substrate"));
        }

        var adaptations = new List<WorldSaveAdaptation>();
        foreach (var dependency in source.ContentDependencies)
        {
            if (!TryCurrentRevision(dependency, live, catalog, composites, out var current))
            {
                return FixedSourceCheck.Fail(
                    WorldSaveStatus.MissingDefinition,
                    new WorldSaveDiagnostic("dependency", dependency.Id, "content_dependency", "a saved content dependency no longer resolves"));
            }

            if (!string.Equals(current, dependency.SemanticRevision, StringComparison.Ordinal))
            {
                adaptations.Add(new WorldSaveAdaptation(
                    dependency.Id,
                    "semantic_revision",
                    "the current revision differs and the rebuilt base still matches"));
            }
        }

        foreach (var required in live.ContentDependencies)
        {
            var listed = source.ContentDependencies.Any(item =>
                string.Equals(item.Kind, required.Kind, StringComparison.Ordinal) &&
                string.Equals(item.Id, required.Id, StringComparison.Ordinal));
            if (listed)
                continue;
            return FixedSourceCheck.Fail(
                WorldSaveStatus.MissingDefinition,
                new WorldSaveDiagnostic(
                    "dependency",
                    required.Id,
                    "content_dependency",
                    "the saved source omits a dependency of the configuration"));
        }

        VoxelDevelopmentSession? scratch = null;
        try
        {
            var established = PlaygroundSession.TryEstablish(source.ConfigurationId, catalog, composites, store: null, out scratch);
            if (established != PlaygroundEstablishStatus.Established || scratch?.WorldSource is null)
                return FixedSourceCheck.Fail(Map(established), DiagnoseResolve(source.ConfigurationId, established));
            if (!string.Equals(scratch.WorldSource.BaseFingerprint, source.BaseFingerprint, StringComparison.Ordinal))
            {
                return FixedSourceCheck.Fail(
                    WorldSaveStatus.IncompatibleBase,
                    new WorldSaveDiagnostic("base", source.ConfigurationId, "base_fingerprint", "the rebuilt configuration base does not match the saved fingerprint"));
            }

            return FixedSourceCheck.Pass(adaptations);
        }
        finally
        {
            scratch?.Detach();
        }
    }

    private static bool SameParameters(
        IReadOnlyList<WorldSourceParameter> saved,
        IReadOnlyList<WorldSourceParameter> live)
    {
        if (saved.Count != live.Count)
            return false;
        var liveByName = new Dictionary<string, string>(live.Count, StringComparer.Ordinal);
        foreach (var parameter in live)
        {
            if (!liveByName.TryAdd(parameter.Name, parameter.Value))
                return false;
        }

        foreach (var parameter in saved)
        {
            if (!liveByName.TryGetValue(parameter.Name, out var value) ||
                !string.Equals(parameter.Value, value, StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private static bool TryCurrentRevision(
        WorldSourceDependency dependency,
        WorldSource live,
        VoxelCatalog catalog,
        CompositeCatalog composites,
        out string revision)
    {
        var match = live.ContentDependencies.FirstOrDefault(item =>
            string.Equals(item.Kind, dependency.Kind, StringComparison.Ordinal) &&
            string.Equals(item.Id, dependency.Id, StringComparison.Ordinal));
        if (!string.IsNullOrEmpty(match.Id))
        {
            revision = match.SemanticRevision;
            return true;
        }

        if (catalog.Current is null)
        {
            revision = "";
            return false;
        }

        try
        {
            switch (dependency.Kind)
            {
                case "voxel" when catalog.Current.TryGet(VoxelDefinitionId.Parse(dependency.Id), out var voxel):
                    revision = voxel.SemanticRevision;
                    return true;
                case "composite" when composites.Current.TryGet(CompositeDefinitionId.Parse(dependency.Id), out var composite):
                    revision = composite.SemanticRevision;
                    return true;
                case "archetype" when composites.Current.TryGet(CompositeArchetypeId.Parse(dependency.Id), out var archetype):
                    revision = archetype.SemanticRevision;
                    return true;
            }
        }
        catch (ArgumentException)
        {
            revision = "";
            return false;
        }

        revision = "";
        return false;
    }

    private static WorldSaveStatus Map(PlaygroundEstablishStatus status) => status switch
    {
        PlaygroundEstablishStatus.MissingContent or PlaygroundEstablishStatus.ContentUnavailable
            => WorldSaveStatus.MissingDefinition,
        _ => WorldSaveStatus.IncompatibleBase
    };

    private static WorldSaveDiagnostic DiagnoseResolve(string configurationId, PlaygroundEstablishStatus status)
    {
        var (stage, relationship) = status switch
        {
            PlaygroundEstablishStatus.UnknownConfiguration => ("base", "unknown_configuration"),
            PlaygroundEstablishStatus.UnsupportedDeclaration => ("base", "declaration"),
            PlaygroundEstablishStatus.InitialStateRejected => ("base", "initial_state"),
            PlaygroundEstablishStatus.ConfigurationMismatch => ("base", "configuration"),
            PlaygroundEstablishStatus.MissingContent => ("dependency", "content_dependency"),
            PlaygroundEstablishStatus.ContentUnavailable => ("dependency", "content_unavailable"),
            _ => ("base", "configuration")
        };
        return new WorldSaveDiagnostic(stage, configurationId, relationship, "the saved configuration could not be rebuilt");
    }
}
