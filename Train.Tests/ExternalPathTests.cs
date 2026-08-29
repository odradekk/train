using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Content;

namespace Train.Tests;

[TestClass]
public sealed class ExternalPathTests
{
    [TestMethod]
    public void Lexical_containment_rejects_parent_and_sibling_escapes()
    {
        var root = Path.Combine(Path.GetTempPath(), "train-path-" + Guid.NewGuid().ToString("N"), "mods");
        Directory.CreateDirectory(root);
        try
        {
            var inside = Path.Combine(root, "pretty", "resources", "copper.png");
            Directory.CreateDirectory(Path.GetDirectoryName(inside)!);
            File.WriteAllBytes(inside, new byte[] { 1, 2, 3 });

            Assert.IsTrue(ExternalPath.IsSafeUnderRoot(root, inside));
            Assert.IsFalse(ExternalPath.IsSafeUnderRoot(root, Path.Combine(root, "..", "outside.json")));
            Assert.IsFalse(ExternalPath.IsSafeUnderRoot(root, Path.GetTempPath()));
            Assert.IsTrue(ExternalPath.IsUnsafe(root, Path.Combine(root, "..", "secret.png")));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public void Physical_mod_root_discovers_only_direct_children_and_ignores_code_files()
    {
        var root = Path.Combine(Path.GetTempPath(), "train-mods-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "notes.txt"), "ignore");
            Directory.CreateDirectory(Path.Combine(root, "nested", "too-deep"));
            File.WriteAllText(
                Path.Combine(root, "nested", "too-deep", ModDiscovery.ManifestFileName),
                ModFixtures.Manifest(ModFixtures.OtherUuid));

            var packageRoot = Path.Combine(root, "pretty-name");
            var resources = Path.Combine(packageRoot, ModDiscovery.ResourcesDirectoryName);
            Directory.CreateDirectory(resources);
            File.WriteAllText(Path.Combine(packageRoot, ModDiscovery.ManifestFileName), ModFixtures.Manifest(ModFixtures.SampleUuid));
            File.WriteAllText(Path.Combine(resources, "copper.goods.json"), ContentFixtures.GoodsFile());
            File.WriteAllBytes(Path.Combine(resources, "plugin.dll"), new byte[] { 0x4d, 0x5a });
            File.WriteAllText(Path.Combine(resources, "script.cs"), "class X {}");
            File.WriteAllText(Path.Combine(resources, "hook.gd"), "extends Node");
            File.WriteAllBytes(Path.Combine(resources, "pack.pck"), new byte[] { 1 });

            var scan = ModDiscovery.Scan(ExternalContent.ListDirectChildren(root));
            Assert.AreEqual(1, scan.AcceptedCount);
            Assert.AreEqual(ModFixtures.SampleUuid, scan.Packages.Single().Namespace);

            var discovery = ExternalContent.ListGoodsFiles(resources);
            CollectionAssert.AreEqual(new[] { "copper.goods.json" }, discovery.Files.ToArray());
            Assert.IsFalse(discovery.Invalid);

            Assert.IsNull(ExternalContent.ReadFile(resources, "plugin.dll", ContentLimits.GoodsFileMaxBytes).Bytes);
            Assert.IsNull(ExternalContent.ReadFile(resources, "script.cs", ContentLimits.GoodsFileMaxBytes).Bytes);
            Assert.IsNull(ExternalContent.ReadFile(resources, "hook.gd", ContentLimits.GoodsFileMaxBytes).Bytes);
            Assert.IsNull(ExternalContent.ReadFile(resources, "pack.pck", ContentLimits.GoodsFileMaxBytes).Bytes);
            Assert.IsNotNull(ExternalContent.ReadFile(resources, "copper.goods.json", ContentLimits.GoodsFileMaxBytes).Bytes);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public void Linked_or_reparse_entries_fail_closed_instead_of_committing_a_partial_package()
    {
        var root = Path.Combine(Path.GetTempPath(), "train-link-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(root, "target");
        var packageRoot = Path.Combine(root, "pretty");
        var resources = Path.Combine(packageRoot, ModDiscovery.ResourcesDirectoryName);
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(resources);
        File.WriteAllText(Path.Combine(packageRoot, ModDiscovery.ManifestFileName), ModFixtures.Manifest(ModFixtures.SampleUuid));
        File.WriteAllText(Path.Combine(resources, "safe.goods.json"), ContentFixtures.GoodsFile());
        File.WriteAllText(Path.Combine(target, "hidden.goods.json"), ContentFixtures.GoodsFile());

        var junction = Path.Combine(resources, "escaped");
        if (!TryCreateJunction(junction, target))
        {
            Assert.IsTrue(ExternalPath.IsUnsafe(resources, Path.Combine(resources, "..", "secret.goods.json")));
            TryDelete(root);
            return;
        }

        try
        {
            Assert.IsTrue(ExternalPath.IsReparsePoint(junction));
            Assert.IsTrue(ExternalPath.IsUnsafe(resources, Path.Combine(junction, "hidden.goods.json")));

            var discovery = ExternalContent.ListGoodsFiles(resources);
            Assert.IsTrue(discovery.Invalid);
            Assert.IsTrue(discovery.Diagnostics.Any(item => item.Code == ContentDiagnosticCodes.UnsafeLinkedPath));
            Assert.IsTrue(discovery.Files.Contains("safe.goods.json"));
            Assert.IsFalse(discovery.Files.Any(file => file.Contains("hidden", StringComparison.Ordinal)));

            var hidden = ExternalContent.ReadFile(resources, "escaped/hidden.goods.json", ContentLimits.GoodsFileMaxBytes);
            Assert.IsTrue(hidden.UnsafeLink);
            Assert.IsNull(hidden.Bytes);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public void Unreadable_mods_root_produces_no_candidates()
    {
        var missing = Path.Combine(Path.GetTempPath(), "train-missing-mods-" + Guid.NewGuid().ToString("N"));
        var blocked = Path.Combine(Path.GetTempPath(), "train-mods-file-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocked, "not-a-directory");
        try
        {
            Assert.IsFalse(ExternalContent.TryListDirectChildren(missing, out var missingChildren, out var missingError));
            Assert.AreEqual(0, missingChildren.Count);
            Assert.IsFalse(string.IsNullOrEmpty(missingError));
            Assert.IsFalse(ExternalContent.TryEnsureDirectory(blocked, out var error));
            Assert.IsFalse(string.IsNullOrEmpty(error));
        }
        finally
        {
            TryDelete(blocked);
        }
    }

    private static bool TryCreateJunction(string link, string target)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /J \"{link}\" \"{target}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (process is null)
                return false;
            process.WaitForExit(5000);
            return Directory.Exists(link) && ExternalPath.IsReparsePoint(link);
        }
        catch
        {
            return false;
        }
    }

    private static void TryDelete(string root)
    {
        try
        {
            if (File.Exists(root))
                File.Delete(root);
            else if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
        catch
        {
            // Best-effort cleanup for Windows temp paths that still have open handles.
        }
    }
}
