using System.Text;
using System.Text.Json;
using Train.Core.Content;
using Train.Domain.Voxels;

namespace Train.Domain.WorldEditor;

public sealed record WorldEditorSaveEntry(
    Guid Id, string Name, DateTime? LastSavedUtc, int? FormatVersion,
    string GeneratorVersion, WorldSource? Source,
    IReadOnlyList<WorldSaveDefinitionRef> ExternalDefinitions, WorldSaveResult Recovery);

/// <summary>Lists local saves and validates an independent candidate before handing it to the editor.</summary>
public sealed class WorldEditorWorkspace(VoxelCatalog catalog, CompositeCatalog composites, FileWorldSaveStore store,
    Func<string, string>? readExample = null)
{
    public WorldSaveDiagnostic? ListFailure { get; private set; }

    public IReadOnlyList<WorldEditorExampleEntry> ListExamples() => WorldEditorExamples.All.Select(example =>
    {
        var read = ReadExample(example.Path, out var document);
        var recovery = read.Succeeded ? ValidateDocument(document) : read;
        return new WorldEditorExampleEntry(example, document, recovery);
    }).ToArray();

    /// <summary>Restores the distributed document, then removes only its file identity before handing off the copy.</summary>
    public WorldSaveResult TryCreateExampleCopy(WorldEditorExample example, out VoxelDevelopmentSession? session)
    {
        session = null;
        var read = ReadExample(example.Path, out var document);
        if (!read.Succeeded)
            return read;
        var candidate = CreateCandidate();
        try
        {
            var restored = candidate.LoadDocument(document);
            if (!restored.Succeeded)
                return restored;
            candidate.MarkUnsavedCopy();
            candidate.PauseSimulation();
            session = candidate;
            return restored with { SaveId = null };
        }
        finally
        {
            if (session is null)
                candidate.Detach();
        }
    }

    private WorldSaveResult ReadExample(string path, out WorldSaveDocument document)
    {
        document = null!;
        // Save-only callers do not need resource I/O; only example hosts supply a reader for the packaged Godot files.
        if (readExample is null)
            return new WorldSaveResult(WorldSaveStatus.Unavailable, Diagnostic: new("example", path, "read", "no distributed-file reader is available"));
        try
        {
            return ParseDocument(readExample(path), path, out document);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new WorldSaveResult(WorldSaveStatus.ReadFailed, Diagnostic: new("example", path, "read", error.Message));
        }
    }

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
                var generator = "";
                WorldSource? source = null;
                IReadOnlyList<WorldSaveDefinitionRef> definitions = Array.Empty<WorldSaveDefinitionRef>();
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
                        generator = document.GeneratorVersion;
                        source = document.Source;
                        definitions = document.ExternalDefinitions;
                    }
                }
                var recovery = TryOpen(id, out var candidate);
                candidate?.Detach();
                entries.Add(new WorldEditorSaveEntry(id, name, saved, format, generator, source, definitions, recovery));
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

    /// <summary>Validates a complete ordinary save, then publishes a new local copy without opening it.</summary>
    public WorldSaveResult Import(string path)
    {
        var read = ReadDocument(path, out var document);
        if (!read.Succeeded)
            return read;
        var validated = ValidateDocument(document);
        if (!validated.Succeeded)
            return validated;
        var copy = document.With(Guid.NewGuid(), document.Vehicles, document.Assemblies);
        if (!store.TryWriteNew(copy.Id, WorldSaveCodec.Serialize(copy), out var status))
            return new WorldSaveResult(status, Diagnostic: new("import", store.PathFor(copy.Id), "write", "the local copy was not published"));
        return new WorldSaveResult(WorldSaveStatus.Saved, copy.Id, copy.DisplayName, Adaptations: validated.Adaptations);
    }

    /// <summary>Exports committed state without changing the session or its save point. Existing targets require explicit permission.</summary>
    public WorldSaveResult Export(VoxelDevelopmentSession session, string path, bool overwrite = false)
    {
        if (session.IsChangingWorld)
            return new WorldSaveResult(WorldSaveStatus.WorldChanging, session.SaveId, session.DisplayName);
        string? candidatePath = null;
        try
        {
            path = Path.GetFullPath(path);
            if (ExternalPath.IsSafeUnderRoot(Path.Combine(store.Root, FileWorldSaveStore.SavesFolder), path))
                return new WorldSaveResult(WorldSaveStatus.WriteFailed,
                    Diagnostic: new("export", path, "working_save", "export must not replace a local working save; choose another location"));
            if (File.Exists(path) && !overwrite)
                return new WorldSaveResult(WorldSaveStatus.OverwriteConfirmationRequired,
                    Diagnostic: new("export", path, "overwrite", "confirm before replacing the existing file"));
            if (!session.TryCaptureSave(session.SaveId ?? Guid.NewGuid(), null, out var document, out var captureStatus))
                return new WorldSaveResult(captureStatus, session.SaveId, session.DisplayName);
            candidatePath = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.candidate");
            FileWorldSaveStore.WriteCandidate(candidatePath, WorldSaveCodec.Serialize(document), FileMode.CreateNew);
            var read = ReadDocument(candidatePath, out var written);
            if (!read.Succeeded)
                return read;
            var validated = ValidateDocument(written);
            if (!validated.Succeeded)
                return validated;
            File.Move(candidatePath, path, overwrite);
            return new WorldSaveResult(WorldSaveStatus.Saved, document.Id, document.DisplayName);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new WorldSaveResult(WorldSaveStatus.WriteFailed, Diagnostic: new("export", path, "write", error.Message));
        }
        finally
        {
            if (candidatePath is not null)
            {
                try { File.Delete(candidatePath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static WorldSaveResult ReadDocument(string path, out WorldSaveDocument document)
    {
        document = null!;
        try
        {
            var json = File.ReadAllText(path, Encoding.UTF8);
            return ParseDocument(json, path, out document);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new WorldSaveResult(WorldSaveStatus.ReadFailed, Diagnostic: new("import", path, "read", error.Message));
        }
    }

    private static WorldSaveResult ParseDocument(string json, string path, out WorldSaveDocument document)
    {
        if (!WorldSaveCodec.TryDeserialize(json, out document, out var status))
            return new WorldSaveResult(status,
                Diagnostic: new("format", path, status.ToString(), "the file is not a supported complete save"));
        return new WorldSaveResult(WorldSaveStatus.Loaded, document.Id, document.DisplayName);
    }

    private WorldSaveResult ValidateDocument(WorldSaveDocument document)
    {
        var candidate = CreateCandidate();
        try
        {
            return candidate.LoadDocument(document);
        }
        finally
        {
            candidate.Detach();
        }
    }

    public WorldSaveResult TryOpen(Guid id, out VoxelDevelopmentSession? session)
    {
        session = null;
        var candidate = CreateCandidate();
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

    private VoxelDevelopmentSession CreateCandidate()
    {
        BlankWorldSource.TryIdentity(1, 1, 1, 0, out var empty);
        var candidate = new VoxelDevelopmentSession(catalog, empty, store, composites);
        candidate.MarkWorldEditorHost();
        return candidate;
    }
}
