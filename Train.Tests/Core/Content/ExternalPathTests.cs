using System.Diagnostics;
// Core 内容模块测试：验证内容发现、解析与重载的无引擎契约。
// 测试输入保持最小，并与旧世界运行态隔离。
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Content;
using Train.Tests;

namespace Train.Tests.Core.Content;

[TestClass]
[TestCategory("family:unit")]
[TestCategory("scope:content")]
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
            File.WriteAllText(Path.Combine(resources, "copper.voxels.json"), ContentFixtures.GoodsFile());
            File.WriteAllBytes(Path.Combine(resources, "plugin.dll"), new byte[] { 0x4d, 0x5a });
            File.WriteAllText(Path.Combine(resources, "script.cs"), "class X {}");
            File.WriteAllText(Path.Combine(resources, "hook.gd"), "extends Node");
            File.WriteAllBytes(Path.Combine(resources, "pack.pck"), new byte[] { 1 });

            var trusted = Directory.GetParent(root)!.FullName;
            var scan = ModDiscovery.Scan(ExternalContent.ListDirectChildren(trusted, root));
            Assert.AreEqual(1, scan.AcceptedCount);
            Assert.AreEqual(ModFixtures.SampleUuid, scan.Packages.Single().Namespace);

            var discovery = ExternalContent.ListVoxelFiles(resources);
            CollectionAssert.AreEqual(new[] { "copper.voxels.json" }, discovery.Files.ToArray());
            Assert.IsFalse(discovery.Invalid);

            Assert.IsNull(ExternalContent.ReadFile(resources, "plugin.dll", ContentLimits.JsonFileMaxBytes).Bytes);
            Assert.IsNull(ExternalContent.ReadFile(resources, "script.cs", ContentLimits.JsonFileMaxBytes).Bytes);
            Assert.IsNull(ExternalContent.ReadFile(resources, "hook.gd", ContentLimits.JsonFileMaxBytes).Bytes);
            Assert.IsNull(ExternalContent.ReadFile(resources, "pack.pck", ContentLimits.JsonFileMaxBytes).Bytes);
            Assert.IsNotNull(ExternalContent.ReadFile(resources, "copper.voxels.json", ContentLimits.JsonFileMaxBytes).Bytes);
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
        File.WriteAllText(Path.Combine(resources, "safe.voxels.json"), ContentFixtures.GoodsFile());
        File.WriteAllText(Path.Combine(target, "hidden.voxels.json"), ContentFixtures.GoodsFile());

        var junction = Path.Combine(resources, "escaped");
        if (!TryCreateDirectoryLink(junction, target))
        {
            TryDelete(root);
            Assert.Inconclusive("Could not create a directory link in this environment.");
            return;
        }

        try
        {
            Assert.IsTrue(ExternalPath.IsReparsePoint(junction));
            Assert.IsTrue(ExternalPath.IsUnsafe(resources, Path.Combine(junction, "hidden.voxels.json")));

            var discovery = ExternalContent.ListVoxelFiles(resources);
            Assert.IsTrue(discovery.Invalid);
            Assert.IsTrue(discovery.Diagnostics.Any(item => item.Code == ContentDiagnosticCodes.UnsafeLinkedPath));
            Assert.IsTrue(discovery.Files.Contains("safe.voxels.json"));
            Assert.IsFalse(discovery.Files.Any(file => file.Contains("hidden", StringComparison.Ordinal)));

            var hidden = ExternalContent.ReadFile(resources, "escaped/hidden.voxels.json", ContentLimits.JsonFileMaxBytes);
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
        var trusted = Path.GetTempPath();
        var missing = Path.Combine(trusted, "train-missing-mods-" + Guid.NewGuid().ToString("N"));
        var blocked = Path.Combine(trusted, "train-mods-file-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocked, "not-a-directory");
        try
        {
            Assert.IsFalse(ExternalContent.TryListDirectChildren(trusted, missing, out var missingChildren, out var missingError));
            Assert.AreEqual(0, missingChildren.Count);
            Assert.IsFalse(string.IsNullOrEmpty(missingError));
            Assert.IsFalse(ExternalContent.TryEnsureDirectory(trusted, blocked, out var error));
            Assert.IsFalse(string.IsNullOrEmpty(error));
            Assert.IsFalse(ExternalContent.TryListDirectChildren(trusted, blocked, out var blockedChildren, out var blockedError));
            Assert.AreEqual(0, blockedChildren.Count);
            Assert.IsFalse(string.IsNullOrEmpty(blockedError));
        }
        finally
        {
            TryDelete(blocked);
        }
    }

    [TestMethod]
    public void Trusted_chain_rejects_a_path_outside_the_trusted_root()
    {
        var trusted = Path.Combine(Path.GetTempPath(), "train-trusted-" + Guid.NewGuid().ToString("N"));
        var outsider = Path.Combine(Path.GetTempPath(), "train-outside-" + Guid.NewGuid().ToString("N"), "mods");
        Directory.CreateDirectory(trusted);
        Directory.CreateDirectory(outsider);
        try
        {
            Assert.IsFalse(ExternalPath.TryValidateTrustedChain(trusted, outsider, out var error));
            Assert.IsFalse(string.IsNullOrEmpty(error));
            Assert.IsFalse(ExternalContent.TryEnsureDirectory(trusted, outsider, out var ensureError));
            Assert.IsFalse(string.IsNullOrEmpty(ensureError));
            Assert.IsFalse(ExternalContent.TryListDirectChildren(trusted, outsider, out var children, out var listError));
            Assert.AreEqual(0, children.Count);
            Assert.IsFalse(string.IsNullOrEmpty(listError));
        }
        finally
        {
            TryDelete(trusted);
            TryDelete(Directory.GetParent(outsider)!.FullName);
        }
    }

    [TestMethod]
    public void Trusted_chain_rejects_a_mods_path_reached_through_a_reparse_ancestor()
    {
        var trusted = Path.Combine(Path.GetTempPath(), "train-trusted-" + Guid.NewGuid().ToString("N"));
        var real = Path.Combine(trusted, "real");
        var junction = Path.Combine(trusted, "linked-parent");
        Directory.CreateDirectory(real);
        if (!TryCreateDirectoryLink(junction, real))
        {
            Assert.IsFalse(ExternalPath.TryValidateTrustedChain(trusted, Path.Combine(trusted, "..", "outside", "mods"), out var escaped));
            Assert.IsFalse(string.IsNullOrEmpty(escaped));
            TryDelete(trusted);
            Assert.Inconclusive("Could not create a directory link ancestor in this environment.");
            return;
        }

        var mods = Path.Combine(junction, "mods");
        Directory.CreateDirectory(mods);
        try
        {
            Assert.IsTrue(ExternalPath.IsReparsePoint(junction));
            Assert.IsFalse(ExternalPath.IsReparsePoint(mods));
            Assert.IsFalse(ExternalPath.TryValidateTrustedChain(trusted, mods, out var error));
            Assert.IsFalse(string.IsNullOrEmpty(error));
            Assert.IsFalse(ExternalContent.TryEnsureDirectory(trusted, mods, out var ensureError));
            Assert.IsFalse(string.IsNullOrEmpty(ensureError));
            Assert.IsFalse(ExternalContent.TryListDirectChildren(trusted, mods, out var children, out var listError));
            Assert.AreEqual(0, children.Count);
            Assert.IsFalse(string.IsNullOrEmpty(listError));
        }
        finally
        {
            TryDelete(trusted);
        }
    }

    private static bool TryCreateDirectoryLink(string link, string target)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                Directory.CreateSymbolicLink(link, target);
                return Directory.Exists(link) && ExternalPath.IsReparsePoint(link);
            }

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
