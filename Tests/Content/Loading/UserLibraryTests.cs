using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content.Loading;
using C = Train.Content.Contracts;

namespace Train.Content.Tests.Loading;

[TestClass]
public sealed class UserLibraryTests
{
    private static readonly C.ContentRoot Library = new("user");
    private const string Good = "11111111-1111-4111-8111-111111111111";
    private const string Missing = "22222222-2222-4222-8222-222222222222";
    private const string Invalid = "33333333-3333-4333-8333-333333333333";
    private const string Broken = "44444444-4444-4444-8444-444444444444";
    private const string WrongId = "55555555-5555-4555-8555-555555555555";
    private const string Unreadable = "66666666-6666-4666-8666-666666666666";

    [TestMethod]
    public void Unusable_files_do_not_hide_valid_entries_or_fail_the_library()
    {
        var fixture = DesignFixtures.Content();
        using var candidate = fixture.Load();
        var view = (ContentView)candidate.View;
        fixture.AddFile(Library, Good + ".blueprint.json", Blueprint(view, Good));
        var missing = JsonNode.Parse(Blueprint(view, Missing))!;
        missing["layout"]!["palette"]![0]!["voxel"] = "core:missing";
        fixture.AddFile(Library, Missing + ".blueprint.json", missing.ToJsonString());
        var invalid = JsonNode.Parse(Blueprint(view, Invalid))!;
        invalid["display"]!["name"] = "";
        fixture.AddFile(Library, Invalid + ".blueprint.json", invalid.ToJsonString());
        fixture.AddFile(Library, Broken + ".blueprint.json", "{broken");
        fixture.AddFile(Library, WrongId + ".blueprint.json", Blueprint(view, Good));
        fixture.SetReadFailure(Library, Unreadable + ".blueprint.json", C.ContentIoFailure.Inaccessible);
        fixture.AddFile(Library, "not-an-id.blueprint.json", "{}");

        var result = UserLibraryLoader.Load(view, Library, fixture);
        var summaries = result.Records.Select(PlayerBlueprint.Summary).Concat(result.Summaries).ToArray();
        Assert.AreEqual(6, summaries.Length);
        Assert.AreEqual(Good, summaries.Single(s => s.Usable).Address.Id.Value);
        Assert.AreEqual(5, summaries.Count(s => !s.Usable));
        Assert.AreEqual(1, result.AuthoringMissing.Select(d => ((C.ContentReference.Blueprint)d.Subject!).Address).Distinct().Count());
        Assert.IsTrue(result.Diagnostics.Any(d => d.Field.StartsWith("not-an-id.blueprint.json", StringComparison.Ordinal)));
        Assert.IsTrue(result.Summaries.Single(s => s.Address.Id.Value == WrongId).Diagnostics.Any(d => d.Field.EndsWith("/id", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Fingerprint_is_order_independent_and_tracks_invalid_bytes_and_failures()
    {
        var fixture = DesignFixtures.Content();
        using var candidate = fixture.Load();
        var view = (ContentView)candidate.View;
        fixture.AddFile(Library, Good + ".blueprint.json", Blueprint(view, Good));
        fixture.AddFile(Library, "invalid.json", "one");
        var first = UserLibraryLoader.Load(view, Library, fixture).Fingerprint;
        fixture.ReverseEnumeration = true;
        Assert.AreEqual(first, UserLibraryLoader.Load(view, Library, fixture).Fingerprint);
        fixture.AddFile(Library, "invalid.json", "two");
        var changed = UserLibraryLoader.Load(view, Library, fixture).Fingerprint;
        Assert.AreNotEqual(first, changed);
        fixture.SetReadFailure(Library, "invalid.json", C.ContentIoFailure.Inaccessible);
        Assert.AreNotEqual(changed, UserLibraryLoader.Load(view, Library, fixture).Fingerprint);
    }

    [TestMethod]
    public void Missing_directory_is_empty_and_other_enumeration_failures_are_reported()
    {
        var fixture = DesignFixtures.Content();
        using var candidate = fixture.Load();
        var view = (ContentView)candidate.View;
        fixture.SetEnumerationFailure(Library, "", C.ContentIoFailure.Missing);
        var missing = UserLibraryLoader.Load(view, Library, fixture);
        Assert.IsTrue(missing.Diagnostics.IsEmpty);
        Assert.IsTrue(missing.Records.IsEmpty);
        fixture.SetEnumerationFailure(Library, "", C.ContentIoFailure.Inaccessible);
        Assert.AreEqual(1, UserLibraryLoader.Load(view, Library, fixture).Diagnostics.Length);
    }

    [TestMethod]
    [TestCategory("integration")]
    public void Real_user_library_files_are_read_and_changes_are_observed()
    {
        var directory = Path.Combine(Path.GetTempPath(), "train-user-library-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var candidate = DesignFixtures.Content().Load();
            var view = (ContentView)candidate.View;
            var file = Path.Combine(directory, Good + ".blueprint.json");
            File.WriteAllText(file, Blueprint(view, Good));
            var root = new C.ContentRoot(directory);
            var files = new LibraryDiskFiles();
            var loaded = UserLibraryLoader.Load(view, root, files);
            Assert.IsTrue(PlayerBlueprint.Summary(loaded.Records.Single()).Usable);
            File.WriteAllText(file, "{}");
            var broken = UserLibraryLoader.Load(view, root, files);
            Assert.AreNotEqual(loaded.Fingerprint, broken.Fingerprint);
            Assert.IsFalse(broken.Summaries.Single().Usable);
            File.Delete(file);
            Assert.IsTrue(UserLibraryLoader.Load(view, root, files).Summaries.IsEmpty);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static string Blueprint(ContentView view, string id)
    {
        var draft = DesignFixtures.Vehicle();
        return PlayerBlueprint.Encode(new(new(C.BlueprintLibrary.User, new(id)), draft, [], PlayerBlueprint.Evaluate(view, draft)));
    }

    private sealed class LibraryDiskFiles : C.IContentFiles
    {
        public C.ContentIoResult<ImmutableArray<C.ContentFileEntry>> Enumerate(C.ContentPath directory) =>
            new C.ContentIoResult<ImmutableArray<C.ContentFileEntry>>.Success(Directory.EnumerateFiles(directory.Root.Key)
                .Select(path => new C.ContentFileEntry(Path.GetFileName(path), C.ContentEntryKind.File)).ToImmutableArray());

        public C.ContentIoResult<ImmutableArray<byte>> Read(C.ContentPath file, long maximumBytes)
        {
            var path = Path.Combine(file.Root.Key, file.RelativePath);
            return new FileInfo(path).Length > maximumBytes
                ? new C.ContentIoResult<ImmutableArray<byte>>.Failure(C.ContentIoFailure.LimitExceeded, "文件超出限制。")
                : new C.ContentIoResult<ImmutableArray<byte>>.Success(File.ReadAllBytes(path).ToImmutableArray());
        }
    }
}
