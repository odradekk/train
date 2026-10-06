using Train.Core.Content.Composites;
using Train.Core.Content.Voxels;
using System.Globalization;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Domain.Commands;
using Train.Domain.Composites;
using Train.Domain.Rails;
using Train.Domain.Voxels;

namespace Train.Tests;

[TestClass]
[TestCategory("family:unit")]
[TestCategory("scope:world-edit")]
public sealed class WorldEditEncodingTests
{
    // Input format version 1 golden vectors. Digests were computed independently from the literal text
    // (PowerShell SHA256 over UTF-8); changing either side is a format change and needs a new version.
    private static readonly (string Name, Func<WorldEdit> Create, string Canonical, string Digest)[] Golden =
    {
        ("voxel.add",
            () => new VoxelAddEdit(new WorldVoxelCoord(60, 3, 20), new VoxelDefinitionId("core", "ferrous-ore"), VoxelOrientation.Identity),
            "{\"input_format_version\":1,\"kind\":\"voxel.add\",\"position\":{\"x\":60,\"y\":3,\"z\":20},\"definition_id\":\"core:ferrous-ore\",\"orientation\":\"px_py\"}",
            "16E0E34100A5B2A6A23344DFB40BA0F855F022012E7E3D38619A3DE0B50EACBC"),
        ("voxel.remove",
            () => new VoxelRemoveEdit(new WorldVoxelCoord(-1, 0, int.MaxValue)),
            "{\"input_format_version\":1,\"kind\":\"voxel.remove\",\"position\":{\"x\":-1,\"y\":0,\"z\":2147483647}}",
            "209294DA3F88C1CAA4EEFD667FF5997DF2FE2C9AEA44897410934CC933CB761C"),
        ("voxel.damage",
            () => new VoxelDamageEdit(new[]
            {
                new VoxelDamageTarget(new WorldVoxelCoord(6, 4, 4), 25),
                new VoxelDamageTarget(new WorldVoxelCoord(4, 4, 4), 100),
                new VoxelDamageTarget(new WorldVoxelCoord(4, 4, 4), int.MinValue)
            }),
            "{\"input_format_version\":1,\"kind\":\"voxel.damage\",\"targets\":[{\"position\":{\"x\":4,\"y\":4,\"z\":4},\"amount\":-2147483648},{\"position\":{\"x\":4,\"y\":4,\"z\":4},\"amount\":100},{\"position\":{\"x\":6,\"y\":4,\"z\":4},\"amount\":25}]}",
            "E02157E135494F6D201224A65BBC5ED177EE4A102BA3ED6164862C2422078C88"),
        ("vehicle.manufacture",
            () => new VehicleManufactureEdit(HollowShellFixture.DefinitionId, Pose(60.5, 2, 0.1, 0, 0, 1)),
            "{\"input_format_version\":1,\"kind\":\"vehicle.manufacture\",\"definition_id\":\"core:dev-hollow-shell\",\"transform\":{\"origin_x\":60.5,\"origin_y\":2,\"origin_z\":0.1,\"right_x\":0,\"right_y\":0,\"right_z\":1,\"up_x\":0,\"up_y\":1,\"up_z\":0}}",
            "ED6A6C9B725520D4E8C459DBDE106C9CD07473C5D6C6C7A10FC16FC844557E7B"),
        ("vehicle.damage",
            () => new VehicleDamageEdit(new[]
            {
                new CompositeDamageTarget(new EntityId(12), new LocalVoxelCoord(1, 0, 1), 30),
                new CompositeDamageTarget(new EntityId(ulong.MaxValue), new LocalVoxelCoord(0, 0, 0), 1),
                new CompositeDamageTarget(new EntityId(3), new LocalVoxelCoord(2, 1, 0), 5)
            }),
            "{\"input_format_version\":1,\"kind\":\"vehicle.damage\",\"targets\":[{\"vehicle_id\":3,\"cell\":{\"x\":2,\"y\":1,\"z\":0},\"amount\":5},{\"vehicle_id\":12,\"cell\":{\"x\":1,\"y\":0,\"z\":1},\"amount\":30},{\"vehicle_id\":18446744073709551615,\"cell\":{\"x\":0,\"y\":0,\"z\":0},\"amount\":1}]}",
            "F71CCA2A5C78F15D8A0C57451773D16B6E74B67B34A5C262C348F705E92AC4E4"),
        ("vehicle.refit",
            () => new VehicleRefitEdit(new EntityId(1), RefitOperations()),
            "{\"input_format_version\":1,\"kind\":\"vehicle.refit\",\"vehicle_id\":1,\"operations\":[{\"op\":\"move\",\"part_id\":7,\"cell\":{\"x\":2,\"y\":5,\"z\":1}},{\"op\":\"add\",\"cell\":{\"x\":1,\"y\":5,\"z\":1},\"definition_id\":\"core:refit-marker\",\"orientation\":\"pz_py\"},{\"op\":\"remove\",\"part_id\":9},{\"op\":\"rotate\",\"part_id\":8,\"orientation\":\"nx_py\"},{\"op\":\"replace\",\"cell\":{\"x\":0,\"y\":0,\"z\":0},\"definition_id\":\"core:refit-replacement\",\"orientation\":\"px_py\"}]}",
            "3D69E8CBA654E58E210C095CFCFA2657B97A355ED7C242FE3E2889FEEA2033BD"),
        ("rail.construct",
            () => new RailConstructEdit(new RailConstructionPlan(
                new[]
                {
                    new PlannedRailSegment(
                        new HorizontalStraightAlignment(64, 2.5, 20, 65.5, 2.5, 20),
                        new[] { new RailVolumeBox(64, 2, 19.25, 65.5, 2.25, 19.5) },
                        new[] { new RailVolumeBox(64.25, 1.75, 19, 64.75, 2, 21) })
                },
                new[] { new PlannedRailConnection(0, RailEnd.End, 1, RailEnd.Start) })),
            "{\"input_format_version\":1,\"kind\":\"rail.construct\",\"segments\":[{\"alignment\":{\"start_x\":64,\"start_y\":2.5,\"start_z\":20,\"end_x\":65.5,\"end_y\":2.5,\"end_z\":20},\"rails\":[{\"min_x\":64,\"min_y\":2,\"min_z\":19.25,\"max_x\":65.5,\"max_y\":2.25,\"max_z\":19.5}],\"sleepers\":[{\"min_x\":64.25,\"min_y\":1.75,\"min_z\":19,\"max_x\":64.75,\"max_y\":2,\"max_z\":21}]}],\"connections\":[{\"from_segment\":0,\"from_end\":\"end\",\"to_segment\":1,\"to_end\":\"start\"}]}",
            "B8BAAE4323A411A9339CDFC9FAD6154B839A7F8B88A2BF3DA3ED6427A3E17E65"),
        ("rail.repair",
            () => new RailRepairEdit(new[] { new WorldVoxelCoord(65, 2, 19), new WorldVoxelCoord(-1, 0, int.MaxValue), new WorldVoxelCoord(65, 2, 19) }),
            "{\"input_format_version\":1,\"kind\":\"rail.repair\",\"cells\":[{\"x\":65,\"y\":2,\"z\":19},{\"x\":-1,\"y\":0,\"z\":2147483647},{\"x\":65,\"y\":2,\"z\":19}]}",
            "7536DC48F51260FCEC507DF7A8C337569DBB925EFBCDED992E63715F0DEDE945"),
        ("train.assemble",
            () => new TrainAssembleEdit(
                HollowShellFixture.DefinitionId,
                Pose(60.5, 2, 0.1, 0, 0, 1),
                new LocalVoxelCoord(0, 1, 2),
                new CompositeDefinitionId("core", "dev-carriage"),
                Pose(60.5, 2, 0.1, 0, 0, 1),
                new LocalVoxelCoord(3, 0, -1)),
            "{\"input_format_version\":1,\"kind\":\"train.assemble\",\"locomotive\":{\"definition_id\":\"core:dev-hollow-shell\",\"transform\":{\"origin_x\":60.5,\"origin_y\":2,\"origin_z\":0.1,\"right_x\":0,\"right_y\":0,\"right_z\":1,\"up_x\":0,\"up_y\":1,\"up_z\":0},\"coupler_cell\":{\"x\":0,\"y\":1,\"z\":2}},\"carriage\":{\"definition_id\":\"core:dev-carriage\",\"transform\":{\"origin_x\":60.5,\"origin_y\":2,\"origin_z\":0.1,\"right_x\":0,\"right_y\":0,\"right_z\":1,\"up_x\":0,\"up_y\":1,\"up_z\":0},\"coupler_cell\":{\"x\":3,\"y\":0,\"z\":-1}}}",
            "661B5A340E3EA75E0C90A46B997260F33347417A87CB3C5CB5E88357596170B1"),
        ("train.start",
            () => new TrainStartEdit(new EntityId(7)),
            "{\"input_format_version\":1,\"kind\":\"train.start\",\"assembly_id\":7}",
            "FB62CD907B33DECDDDC8CF23053ECFF60ED03C0BDACF0B1454A6CB7BF029A843"),
        ("train.stop",
            () => new TrainStopEdit(new EntityId(ulong.MaxValue)),
            "{\"input_format_version\":1,\"kind\":\"train.stop\",\"assembly_id\":18446744073709551615}",
            "0BB581D76A7E804CBFFA7D61635AF230D63E238F6DE01357C1BEC562C8031D53"),
        ("content.publish",
            () => new ContentPublishEdit("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF"),
            "{\"input_format_version\":1,\"kind\":\"content.publish\",\"content_digest\":\"0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF\"}",
            "C0318AACC4275ED027A0C46989447ABBFE44EC53D4B5C1CE5674657B77D01FC4"),
        ("voxel.cell-set",
            () => new VoxelCellSetEdit(new[]
            {
                new VoxelCellTarget(new WorldVoxelCoord(1, 2, 3), VoxelCellOperation.Add,
                    new VoxelCellContent(new VoxelDefinitionId("core", "ferrous-ore"), VoxelOrientation.Identity)),
                new VoxelCellTarget(new WorldVoxelCoord(4, 5, 6), VoxelCellOperation.Remove, null)
            }),
            "{\"input_format_version\":1,\"kind\":\"voxel.cell-set\",\"targets\":[{\"position\":{\"x\":1,\"y\":2,\"z\":3},\"operation\":\"add\",\"content\":{\"definition_id\":\"core:ferrous-ore\",\"orientation\":\"px_py\"}},{\"position\":{\"x\":4,\"y\":5,\"z\":6},\"operation\":\"remove\",\"content\":null}]}",
            "3B1586B3298D14EDC6858E9B6304E4ECCC622159DD9ABE5AE494196B65E3A207"),
        ("voxel.cell-restore",
            () => new VoxelCellRestoreEdit(new[]
            {
                new VoxelCellRestoreTarget(new WorldVoxelCoord(4, 5, 6),
                    new VoxelCellState(new VoxelDefinitionId("core", "ferrous-ore"), VoxelOrientation.Identity, 100), null),
                new VoxelCellRestoreTarget(new WorldVoxelCoord(1, 2, 3), null,
                    new VoxelCellState(new VoxelDefinitionId("core", "ferrous-ore"), VoxelOrientation.Yaw90, 75))
            }),
            "{\"input_format_version\":1,\"kind\":\"voxel.cell-restore\",\"targets\":[{\"position\":{\"x\":1,\"y\":2,\"z\":3},\"expected\":null,\"content\":{\"definition_id\":\"core:ferrous-ore\",\"orientation\":\"pz_py\",\"current_durability\":75}},{\"position\":{\"x\":4,\"y\":5,\"z\":6},\"expected\":{\"definition_id\":\"core:ferrous-ore\",\"orientation\":\"px_py\",\"current_durability\":100},\"content\":null}]}",
            "9C0BCFB844B1B77F32A3EA65504BAC15C79640BB10EAAD69D1A7A2F144399BE9")
    };

    private const string GoldenRecord =
        "{\"edit_id\":1,\"accepted_step\":0,\"accepted_sequence\":0,\"target_step\":0,\"state\":\"executed\",\"executed_step\":0,\"executed_sequence\":0,\"status\":\"Added\",\"edit\":{\"input_format_version\":1,\"kind\":\"voxel.add\",\"position\":{\"x\":60,\"y\":3,\"z\":20},\"definition_id\":\"core:ferrous-ore\",\"orientation\":\"px_py\"}}";

    private const string GoldenRecordDigest = "01F384A6C4982DD98AF20F8A16D9B410A667E4CC6C3526C7EDE88ED043AF9D9E";

    [TestMethod]
    public void Every_edit_kind_matches_its_fixed_golden_vector_and_round_trips()
    {
        foreach (var (name, create, canonical, digest) in Golden)
        {
            var edit = create();
            Assert.AreEqual(name, edit.WireKind);
            Assert.AreEqual(canonical, edit.Canonical, name);
            CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(canonical), WorldEditCodec.EncodeUtf8(edit), name);
            Assert.AreEqual(digest, edit.Digest, name);
            Assert.IsTrue(WorldEditCodec.TryDecode(canonical, out var decoded, out var status), $"{name} {status}");
            Assert.AreEqual(canonical, decoded.Canonical, name);
            Assert.AreEqual(edit, decoded, name);
        }

        var record = new WorldEditRecord(
            1,
            Golden[0].Create(),
            new SimulationPosition(0, 0),
            0,
            WorldEditState.Executed,
            new SimulationPosition(0, 0),
            "Added");
        Assert.AreEqual(GoldenRecord, record.Canonical);
        Assert.AreEqual(GoldenRecordDigest, WorldEditCodec.Digest(record.Canonical));
    }

    [TestMethod]
    public void Unordered_target_sets_sort_while_refit_operations_keep_their_landing_order()
    {
        var a = new VoxelDamageTarget(new WorldVoxelCoord(1, 2, 3), 10);
        var b = new VoxelDamageTarget(new WorldVoxelCoord(1, 2, 2), 10);
        Assert.AreEqual(new VoxelDamageEdit(new[] { a, b }).Canonical, new VoxelDamageEdit(new[] { b, a }).Canonical);

        var v = new CompositeDamageTarget(new EntityId(5), new LocalVoxelCoord(0, 1, 0), 3);
        var w = new CompositeDamageTarget(new EntityId(4), new LocalVoxelCoord(9, 9, 9), 3);
        Assert.AreEqual(new VehicleDamageEdit(new[] { v, w }).Canonical, new VehicleDamageEdit(new[] { w, v }).Canonical);

        var restoreA = new VoxelCellRestoreTarget(new WorldVoxelCoord(1, 2, 3), null, null);
        var restoreB = new VoxelCellRestoreTarget(new WorldVoxelCoord(1, 2, 2), null, null);
        Assert.AreEqual(
            new VoxelCellRestoreEdit(new[] { restoreA, restoreB }).Canonical,
            new VoxelCellRestoreEdit(new[] { restoreB, restoreA }).Canonical);

        var forward = RefitOperations();
        var reversed = forward.Reverse().ToArray();
        Assert.AreNotEqual(
            new VehicleRefitEdit(new EntityId(1), forward).Canonical,
            new VehicleRefitEdit(new EntityId(1), reversed).Canonical);
    }

    [TestMethod]
    public void Encoding_and_decoding_do_not_depend_on_the_current_culture()
    {
        var expected = Golden.Select(item => item.Canonical).ToArray();
        var original = CultureInfo.CurrentCulture;
        var originalUi = CultureInfo.CurrentUICulture;
        try
        {
            foreach (var name in new[] { "de-DE", "tr-TR", "fr-FR", "ar-SA", "fa-IR", "th-TH" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);
                CollectionAssert.AreEqual(expected, Golden.Select(item => item.Create().Canonical).ToArray(), name);
                foreach (var canonical in expected)
                {
                    Assert.IsTrue(WorldEditCodec.TryDecode(canonical, out var decoded, out _), name);
                    Assert.AreEqual(canonical, decoded.Canonical, name);
                }
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
            CultureInfo.CurrentUICulture = originalUi;
        }
    }

    [TestMethod]
    public void Doubles_round_trip_losslessly_including_negative_zero_and_extremes()
    {
        var values = new[]
        {
            0.1, 1.0 / 3.0, -0.0, 0.0, double.Epsilon, -double.Epsilon, double.MaxValue, double.MinValue,
            1e21, 1e-7, 123456789.123456789, 2.2250738585072014E-308, 8.5, -80.25
        };
        foreach (var value in values)
        {
            var edit = new VehicleManufactureEdit(HollowShellFixture.DefinitionId, Pose(value, value, value, 1, 0, 0));
            Assert.IsTrue(WorldEditCodec.TryDecode(edit.Canonical, out var decoded, out var status), $"{value:R} {status}");
            var transform = ((VehicleManufactureEdit)decoded).Transform;
            Assert.AreEqual(BitConverter.DoubleToInt64Bits(value), BitConverter.DoubleToInt64Bits(transform.OriginX), value.ToString("R", CultureInfo.InvariantCulture));
            Assert.AreEqual(edit.Canonical, decoded.Canonical);
        }

        StringAssert.Contains(new VehicleManufactureEdit(HollowShellFixture.DefinitionId, Pose(-0.0, 1e21, 0.1, 1, 0, 0)).Canonical,
            "\"origin_x\":-0,\"origin_y\":1E+21,\"origin_z\":0.1");
    }

    [TestMethod]
    public void Non_finite_values_are_rejected_on_both_sides()
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => WorldEditCodec.FormatDouble(double.NaN));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => WorldEditCodec.FormatDouble(double.PositiveInfinity));
        var canonical = Golden[3].Canonical;
        AssertDecode(canonical.Replace("\"origin_x\":60.5", "\"origin_x\":1e400", StringComparison.Ordinal), WorldEditDecodeStatus.Invalid);
        AssertDecode(canonical.Replace("\"origin_x\":60.5", "\"origin_x\":NaN", StringComparison.Ordinal), WorldEditDecodeStatus.Invalid);
        AssertDecode(canonical.Replace("\"origin_x\":60.5", "\"origin_x\":\"60.5\"", StringComparison.Ordinal), WorldEditDecodeStatus.Invalid);
        AssertDecode(canonical.Replace("\"right_z\":1", "\"right_z\":2", StringComparison.Ordinal), WorldEditDecodeStatus.Invalid);
    }

    [TestMethod]
    public void Integer_bit_widths_and_signs_are_enforced()
    {
        var remove = Golden[1].Canonical;
        AssertDecode(remove.Replace("2147483647", "2147483648", StringComparison.Ordinal), WorldEditDecodeStatus.Invalid);
        AssertDecode(remove.Replace("\"x\":-1", "\"x\":-2147483649", StringComparison.Ordinal), WorldEditDecodeStatus.Invalid);
        AssertDecode(remove.Replace("\"y\":0", "\"y\":0.0", StringComparison.Ordinal), WorldEditDecodeStatus.Invalid);
        AssertDecode(remove.Replace("\"y\":0", "\"y\":1e0", StringComparison.Ordinal), WorldEditDecodeStatus.Invalid);

        var damage = Golden[4].Canonical;
        AssertDecode(damage.Replace("18446744073709551615", "18446744073709551616", StringComparison.Ordinal), WorldEditDecodeStatus.Invalid);
        AssertDecode(damage.Replace("\"vehicle_id\":3", "\"vehicle_id\":-3", StringComparison.Ordinal), WorldEditDecodeStatus.Invalid);
        AssertDecode(damage.Replace("\"vehicle_id\":3", "\"vehicle_id\":0", StringComparison.Ordinal), WorldEditDecodeStatus.Invalid);
        AssertDecode(Golden[5].Canonical.Replace("\"part_id\":9", "\"part_id\":0", StringComparison.Ordinal), WorldEditDecodeStatus.Invalid);
        Assert.ThrowsException<ArgumentException>(() => new VehicleRefitEdit(default, Array.Empty<CompositeRefitOperation>()));
    }

    [TestMethod]
    public void Unknown_required_formats_and_non_canonical_identifiers_are_rejected()
    {
        var add = Golden[0].Canonical;
        AssertDecode(add.Replace("\"input_format_version\":1", "\"input_format_version\":2", StringComparison.Ordinal), WorldEditDecodeStatus.UnknownFormat);
        AssertDecode(add.Replace("\"input_format_version\":1,", "", StringComparison.Ordinal), WorldEditDecodeStatus.UnknownFormat);
        AssertDecode(add.Replace("\"kind\":\"voxel.add\"", "\"kind\":\"voxel.explode\"", StringComparison.Ordinal), WorldEditDecodeStatus.UnknownFormat);
        AssertDecode(add.Replace("\"kind\":\"voxel.add\"", "\"kind\":\"Voxel.Add\"", StringComparison.Ordinal), WorldEditDecodeStatus.UnknownFormat);
        AssertDecode(add.Insert(add.Length - 1, ",\"priority\":1"), WorldEditDecodeStatus.UnknownFormat);
        AssertDecode(add.Replace("\"z\":20}", "\"z\":20,\"w\":0}", StringComparison.Ordinal), WorldEditDecodeStatus.UnknownFormat);

        var cells = Golden.Single(item => item.Name == "voxel.cell-set").Canonical;
        AssertDecode(cells.Replace("\"operation\":\"add\"", "\"operation\":\"paint\"", StringComparison.Ordinal), WorldEditDecodeStatus.UnknownFormat);
        AssertDecode(cells.Insert(cells.IndexOf(",\"content\"", StringComparison.Ordinal), ",\"expected\":\"empty\""), WorldEditDecodeStatus.UnknownFormat);
        AssertDecode(add.Insert(add.Length - 1, ",\"orientation\":\"px_py\""), WorldEditDecodeStatus.UnknownFormat);
        AssertDecode(Golden[5].Canonical.Replace("\"op\":\"remove\"", "\"op\":\"weld\"", StringComparison.Ordinal), WorldEditDecodeStatus.UnknownFormat);
        AssertDecode(add.Replace("core:ferrous-ore", "Core:ferrous-ore", StringComparison.Ordinal), WorldEditDecodeStatus.Invalid);
        AssertDecode(add.Replace("core:ferrous-ore", " core:ferrous-ore", StringComparison.Ordinal), WorldEditDecodeStatus.Invalid);
        AssertDecode(add.Replace("px_py", "PX_PY", StringComparison.Ordinal), WorldEditDecodeStatus.Invalid);
        AssertDecode(add.Replace(",\"orientation\":\"px_py\"", "", StringComparison.Ordinal), WorldEditDecodeStatus.Invalid);
        AssertDecode("[1]", WorldEditDecodeStatus.Invalid);
        AssertDecode("{", WorldEditDecodeStatus.Invalid);
    }

    [TestMethod]
    public void Cell_target_construction_rejects_unencodable_values()
    {
        var ore = new VoxelDefinitionId("core", "ferrous-ore");
        Assert.ThrowsException<ArgumentException>(() => new VoxelCellContent(default, VoxelOrientation.Identity));
        Assert.ThrowsException<ArgumentException>(() => new VoxelCellContent(ore, default));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new VoxelCellSetEdit(new[]
        {
            new VoxelCellTarget(default, (VoxelCellOperation)99,
                new VoxelCellContent(ore, VoxelOrientation.Identity))
        }));
        Assert.ThrowsException<ArgumentException>(() => new VoxelCellState(default, VoxelOrientation.Identity, 1));
        Assert.ThrowsException<ArgumentException>(() => new VoxelCellState(ore, default, 1));
    }

    [TestMethod]
    public void Restore_payload_rejects_malformed_states_and_unknown_fields()
    {
        var restore = Golden.Single(item => item.Name == "voxel.cell-restore").Canonical;
        AssertDecode(restore.Replace("\"current_durability\":75", "\"current_durability\":75.0", StringComparison.Ordinal), WorldEditDecodeStatus.Invalid);
        AssertDecode(restore.Replace("\"orientation\":\"pz_py\"", "\"orientation\":\"PX_PY\"", StringComparison.Ordinal), WorldEditDecodeStatus.Invalid);
        AssertDecode(restore.Replace("\"current_durability\":75", "\"durability\":75", StringComparison.Ordinal), WorldEditDecodeStatus.Invalid);
        AssertDecode(restore.Replace("\"expected\":null", "\"expected\":false", StringComparison.Ordinal), WorldEditDecodeStatus.Invalid);
        AssertDecode(restore.Insert(restore.IndexOf(",\"content\"", StringComparison.Ordinal), ",\"runtime_id\":7"), WorldEditDecodeStatus.UnknownFormat);
    }

    [TestMethod]
    public void Malformed_version_or_kind_fields_are_invalid_while_unsupported_values_are_unknown_formats()
    {
        var remove = Golden[1].Canonical;
        string Version(string text) => remove.Replace("\"input_format_version\":1", $"\"input_format_version\":{text}", StringComparison.Ordinal);
        AssertDecode(Version("2147483648"), WorldEditDecodeStatus.Invalid);
        AssertDecode(Version("-2147483649"), WorldEditDecodeStatus.Invalid);
        AssertDecode(Version("1.5"), WorldEditDecodeStatus.Invalid);
        AssertDecode(Version("\"1\""), WorldEditDecodeStatus.Invalid);
        AssertDecode(Version("null"), WorldEditDecodeStatus.Invalid);
        AssertDecode(remove.Replace("\"kind\":\"voxel.remove\"", "\"kind\":5", StringComparison.Ordinal), WorldEditDecodeStatus.Invalid);

        AssertDecode(Version("2147483647"), WorldEditDecodeStatus.UnknownFormat);
        AssertDecode(Version("-1"), WorldEditDecodeStatus.UnknownFormat);
        AssertDecode(Version("0"), WorldEditDecodeStatus.UnknownFormat);
        AssertDecode(remove.Replace("\"kind\":\"voxel.remove\"", "\"kind\":\"voxel.melt\"", StringComparison.Ordinal), WorldEditDecodeStatus.UnknownFormat);
        AssertDecode(remove.Replace("\"kind\":\"voxel.remove\",", "", StringComparison.Ordinal), WorldEditDecodeStatus.UnknownFormat);
    }

    [TestMethod]
    public void Rail_and_train_payloads_separate_invalid_values_from_unknown_formats()
    {
        var construct = Golden[6].Canonical;
        AssertDecode(construct.Replace("\"from_end\":\"end\"", "\"from_end\":\"middle\"", StringComparison.Ordinal), WorldEditDecodeStatus.Invalid);
        AssertDecode(construct.Replace("\"start_y\":2.5", "\"start_y\":1e400", StringComparison.Ordinal), WorldEditDecodeStatus.Invalid);
        AssertDecode(construct.Replace("\"from_segment\":0", "\"from_segment\":2147483648", StringComparison.Ordinal), WorldEditDecodeStatus.Invalid);
        AssertDecode(construct.Replace(",\"sleepers\":[{", ",\"sleepers\":[{\"layer\":0,", StringComparison.Ordinal), WorldEditDecodeStatus.UnknownFormat);
        AssertDecode(construct.Replace(",\"connections\":", ",\"curvature\":0,\"connections\":", StringComparison.Ordinal), WorldEditDecodeStatus.UnknownFormat);
        AssertDecode(construct.Replace("\"kind\":\"rail.construct\"", "\"kind\":\"rail.curve\"", StringComparison.Ordinal), WorldEditDecodeStatus.UnknownFormat);

        var repair = Golden[7].Canonical;
        AssertDecode(repair.Replace("\"y\":2", "\"y\":2147483648", StringComparison.Ordinal), WorldEditDecodeStatus.Invalid);
        AssertDecode(repair.Replace("\"cells\":[", "\"cells\":[1,", StringComparison.Ordinal), WorldEditDecodeStatus.Invalid);

        var assemble = Golden[8].Canonical;
        AssertDecode(assemble.Replace("core:dev-carriage", "Core:dev-carriage", StringComparison.Ordinal), WorldEditDecodeStatus.Invalid);
        AssertDecode(assemble.Replace("\"coupler_cell\":{\"x\":3", "\"coupler_cell\":{\"x\":3.5", StringComparison.Ordinal), WorldEditDecodeStatus.Invalid);
        AssertDecode(assemble.Replace("\"carriage\":{", "\"tender\":{},\"carriage\":{", StringComparison.Ordinal), WorldEditDecodeStatus.UnknownFormat);

        var start = Golden[9].Canonical;
        AssertDecode(start.Replace("\"assembly_id\":7", "\"assembly_id\":0", StringComparison.Ordinal), WorldEditDecodeStatus.Invalid);
        AssertDecode(start.Replace("\"assembly_id\":7", "\"assembly_id\":-7", StringComparison.Ordinal), WorldEditDecodeStatus.Invalid);
        AssertDecode(start.Replace("\"assembly_id\":7", "\"assembly_id\":7,\"heading\":\"reverse\"", StringComparison.Ordinal), WorldEditDecodeStatus.UnknownFormat);
        AssertDecode(Golden[10].Canonical.Replace("18446744073709551615", "18446744073709551616", StringComparison.Ordinal), WorldEditDecodeStatus.Invalid);
        Assert.ThrowsException<ArgumentException>(() => new TrainStartEdit(default));

        var publish = Golden[11].Canonical;
        AssertDecode(publish.Replace("ABCDEF\"", "abcdef\"", StringComparison.Ordinal), WorldEditDecodeStatus.Invalid);
        AssertDecode(publish.Replace("ABCDEF\"", "ABCDE\"", StringComparison.Ordinal), WorldEditDecodeStatus.Invalid);
        Assert.ThrowsException<ArgumentException>(() => new ContentPublishEdit("abc"));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new RailConstructEdit(new RailConstructionPlan(
            new[] { new PlannedRailSegment(new HorizontalStraightAlignment(double.NaN, 0, 0, 1, 0, 0), Array.Empty<RailVolumeBox>(), Array.Empty<RailVolumeBox>()) },
            Array.Empty<PlannedRailConnection>())));
    }

    [TestMethod]
    public void Rail_repair_cells_keep_their_submitted_order()
    {
        var a = new WorldVoxelCoord(1, 2, 3);
        var b = new WorldVoxelCoord(1, 2, 2);
        Assert.AreNotEqual(new RailRepairEdit(new[] { a, b }).Canonical, new RailRepairEdit(new[] { b, a }).Canonical);
    }

    [TestMethod]
    public void Decoding_accepts_any_field_order_and_re_encodes_canonically()
    {
        const string reordered =
            "{\"orientation\":\"px_py\",\"definition_id\":\"core:ferrous-ore\",\"position\":{\"z\":20,\"x\":60,\"y\":3},\"kind\":\"voxel.add\",\"input_format_version\":1}";
        Assert.IsTrue(WorldEditCodec.TryDecode(reordered, out var decoded, out _));
        Assert.AreEqual(Golden[0].Canonical, decoded.Canonical);
    }

    internal static CompositeRefitOperation[] RefitOperations() => new[]
    {
        CompositeRefitOperation.Move(new EntityId(7), new LocalVoxelCoord(2, 5, 1)),
        CompositeRefitOperation.Add(new LocalVoxelCoord(1, 5, 1), new VoxelDefinitionId("core", "refit-marker"), VoxelOrientation.Yaw90),
        CompositeRefitOperation.Remove(new EntityId(9)),
        CompositeRefitOperation.Rotate(new EntityId(8), new VoxelOrientation(VoxelAxis.NegativeX, VoxelAxis.PositiveY)),
        CompositeRefitOperation.Replace(new LocalVoxelCoord(0, 0, 0), new VoxelDefinitionId("core", "refit-replacement"), VoxelOrientation.Identity)
    };

    private static CompositeTransform Pose(double x, double y, double z, double rightX, double rightY, double rightZ) =>
        CompositeTransform.TryRestore(x, y, z, rightX, rightY, rightZ, 0, 1, 0, out var transform)
            ? transform
            : throw new InvalidOperationException("fixture pose is not rigid");

    private static void AssertDecode(string json, WorldEditDecodeStatus expected)
    {
        Assert.IsFalse(WorldEditCodec.TryDecode(json, out _, out var status), json);
        Assert.AreEqual(expected, status, json);
    }
}
