using System.Text.Json;
using Train.Content;
using Train.Domain.Voxels;

namespace Train.Domain.WorldEditor;

public sealed record WorldEditorSaveEntry(
    Guid Id, string Name, DateTime? LastSavedUtc, int? FormatVersion,
    string SourceVersion, IReadOnlyList<string> Dependencies, WorldSaveResult Recovery);

/// <summary>Lists local saves and validates an independent candidate before handing it to the editor.</summary>
public sealed class WorldEditorWorkspace(VoxelCatalog catalog, CompositeCatalog composites, FileWorldSaveStore store)
{
    public WorldSaveDiagnostic? ListFailure { get; private set; }

    public IReadOnlyList<WorldEditorSaveEntry> ListSaves()
    {
        ListFailure = null;
        var entries = new List<WorldEditorSaveEntry>();
        try
        {
            var root = Path.Combine(store.Root, FileWorldSaveStore.SavesFolder);
            if (!Directory.Exists(root))
                return entries;
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                if (!Guid.TryParse(Path.GetFileName(directory), out var id))
                    continue;
                var path = store.PathFor(id);
                if (!File.Exists(path))
                    continue;
                DateTime? saved = null;
                try { saved = File.GetLastWriteTimeUtc(path); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                var name = FileWorldSaveStore.FormatId(id);
                int? format = null;
                var source = "";
                var dependencies = new List<string>();
                if (store.TryRead(id, out var json, out _))
                {
                    // Keep useful header information even when a newer format cannot be restored.
                    try
                    {
                        using var header = JsonDocument.Parse(json);
                        var value = header.RootElement;
                        if (value.ValueKind == JsonValueKind.Object)
                        {
                            if (value.TryGetProperty("display_name", out var title) && title.ValueKind == JsonValueKind.String &&
                                WorldSaveDocument.TryNormalizeDisplayName(title.GetString(), out var normalized))
                                name = normalized;
                            if (value.TryGetProperty("save_format_version", out var version) && version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out var number))
                                format = number;
                        }
                    }
                    catch (JsonException) { }
                    if (WorldSaveCodec.TryDeserialize(json, out var document, out _))
                    {
                        name = document.DisplayName;
                        format = document.FormatVersion;
                        source = document.Source is { } origin
                            ? $"{origin.Kind}/{origin.ConfigurationId} v{origin.ConfigurationVersion}"
                            : document.GeneratorVersion;
                        dependencies.AddRange(document.ExternalDefinitions.Select(item => $"{item.DefinitionId} @ {item.SemanticRevision}"));
                        if (document.Source is { } declared)
                            dependencies.AddRange(declared.ContentDependencies.Select(item => $"{item.Kind}:{item.Id} @ {item.SemanticRevision}"));
                    }
                }
                var recovery = TryOpen(id, out var candidate);
                candidate?.Detach();
                entries.Add(new WorldEditorSaveEntry(id, name, saved, format, source, dependencies.Distinct().ToArray(), recovery));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ListFailure = new WorldSaveDiagnostic("workspace", "saves", "read", error.Message);
        }
        return entries.OrderByDescending(item => item.LastSavedUtc).ThenBy(item => item.Id).ToArray();
    }

    public VoxelDevelopmentSession NewBlank(VoxelWorldIdentity identity) =>
        BlankWorldSource.Establish(identity, catalog, composites, store);

    public WorldSaveResult TryOpen(Guid id, out VoxelDevelopmentSession? session)
    {
        session = null;
        BlankWorldSource.TryIdentity(1, 1, 1, 0, out var empty);
        var candidate = new VoxelDevelopmentSession(catalog, empty, store, composites);
        candidate.MarkWorldEditorHost();
        try
        {
            var result = candidate.Load(id);
            if (!result.Succeeded)
                return result;
            candidate.PauseSimulation();
            session = candidate;
            return result;
        }
        finally
        {
            if (session is null)
                candidate.Detach();
        }
    }
}
