import copy
import importlib.util
import json
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
spec = importlib.util.spec_from_file_location("scene_scale", Path(__file__).resolve().parents[1] / "scene-scale.py")
scene_scale = importlib.util.module_from_spec(spec)
spec.loader.exec_module(scene_scale)


class GeneratedContentTests(unittest.TestCase):
    def test_generated_json_has_platform_independent_utf8_lf_bytes(self):
        with tempfile.TemporaryDirectory() as temporary:
            output = Path(temporary) / "input.json"
            scene_scale.write(output, {"value": "中"})
            self.assertEqual('{"value":"中"}\n'.encode("utf-8"), output.read_bytes())

    def test_modified_ground_roundtrips_within_the_content_json_file_limit(self):
        ground = scene_scale.read(Path(__file__).resolve().parents[3] /
                                  "builtin/core/resources/blueprints/core-city-ground.blueprint.json")
        ground["appearance"]["flatten"] = {"from": [-1312, -1216], "to": [1983, 1599], "height": 1}
        with tempfile.TemporaryDirectory(prefix="train-scale-json-") as temporary:
            generated = Path(temporary) / "ground.json"
            scene_scale.write(generated, ground)
            self.assertEqual(ground, json.loads(generated.read_text(encoding="utf-8")))
            # ContentLimits.JsonFileMaxBytes is one MiB; formatting cannot consume that budget.
            self.assertLessEqual(generated.stat().st_size, 1024 * 1024)


class ScaleInputValidationTests(unittest.TestCase):
    def setUp(self):
        self.baseline = {"far": {"N": 100}, "near96": {"N": 10, "sha256": "buildings"},
                         "groundNear96": {"N": 20, "sha256": "ground"}}
        self.stats = copy.deepcopy(self.baseline)
        self.stats["far"]["N"] = 1000
        self.stats["businessObjectsWithin32m"] = 1

    def test_valid_far_growth_keeps_the_origin_near_content(self):
        scene_scale.validate_variant(self.baseline, self.stats, 10)

    def test_near_content_drift_cannot_be_reported_as_a_scale_point(self):
        for field in ("near96", "groundNear96"):
            with self.subTest(field=field):
                changed = copy.deepcopy(self.stats)
                changed[field]["sha256"] = "different"
                with self.assertRaisesRegex(ValueError, "near_content_changed"):
                    scene_scale.validate_variant(self.baseline, changed, 10)

    def test_insufficient_far_growth_is_rejected(self):
        self.stats["far"]["N"] = 999
        with self.assertRaisesRegex(ValueError, "far_growth_insufficient"):
            scene_scale.validate_variant(self.baseline, self.stats, 10)

    def test_an_empty_near_view_is_rejected_even_when_the_hash_matches(self):
        self.stats["businessObjectsWithin32m"] = 0
        with self.assertRaisesRegex(ValueError, "near_business_missing"):
            scene_scale.validate_variant(self.baseline, self.stats, 10)

    def test_both_mirrored_building_groups_stay_outside_the_node_origin_near_circle(self):
        slots = scene_scale.slots_for({"plots": []}, [0, 0])
        # (0,1) 的镜像组在 z=-128，地块包围盒会进入原点周围 192 格。
        self.assertNotIn((0, 1), slots)
        self.assertIn((0, 2), slots)

    def test_candidate_groups_cannot_overlap_an_original_plot(self):
        slots = scene_scale.slots_for({"plots": [{"size": "giant", "at": [672, 1, 384]}]}, [0, 0])
        self.assertNotIn((1, 0), slots)
        self.assertIn((2, 0), slots)


class CrossingObservationTests(unittest.TestCase):
    def setUp(self):
        self.item = {"id": {"Value": 1}, "category": "crossing_boundary",
                     "worldPose": {"Position": {"X": 0, "Y": 0, "Z": 0}},
                     "worldBounds": {"Minimum": {"X": 0}, "Maximum": {"X": 256}},
                     "localTriangles": 3, "triangleWorldCoordinatesMaterialSha256": "original",
                     "nonRailTriangles": 3, "nonRailTriangleWorldCoordinatesMaterialSha256": "original"}

    def variant(self, multiple, snapshots):
        return {"multiple": multiple, "local96": {"seconds": 999, "stability_samples": [
            {"first": {"seconds": seconds}, "firstGeometry": {"objects": objects},
             "second": None, "secondGeometry": None} for seconds, objects in snapshots]}}

    def test_cross_run_ids_and_hashes_do_not_split_the_same_spatial_object(self):
        changed = {**self.item, "id": {"Value": 900}, "localTriangles": 9,
                   "triangleWorldCoordinatesMaterialSha256": "different", "nonRailTriangles": 9,
                   "nonRailTriangleWorldCoordinatesMaterialSha256": "different"}
        result = scene_scale.crossing_observations([
            self.variant(1, [(10, [self.item])]), self.variant(100, [(20, [changed])])])
        self.assertEqual(1, len(result["objects"]))
        scales = result["objects"][0]["scales"]
        self.assertEqual([1, 100], [scale["multiple"] for scale in scales])
        self.assertEqual([3, 9], [scale["observations"][0]["objects"][0]["localTriangles"] for scale in scales])
        self.assertEqual(["original", "different"], [
            scale["observations"][0]["objects"][0]["triangleWorldCoordinatesMaterialSha256"] for scale in scales])

    def test_first_observation_keeps_the_prior_absence_without_claiming_exact_publication(self):
        result = scene_scale.crossing_observations([
            self.variant(1, [(5, []), (10, []), (20, [self.item]), (30, [self.item])])])
        self.assertEqual(1, len(result["objects"]))
        scale = result["objects"][0]["scales"][0]
        self.assertEqual(20, scale["first_observed_seconds"])
        self.assertEqual(10, scale["last_not_observed_before_first_seconds"])
        self.assertEqual("published_geometry_observed", scale["publication_state"])

    def test_missing_geometry_is_unknown_and_null_repeat_is_not_an_observation(self):
        result = scene_scale.crossing_observations([
            self.variant(1, [(10, [self.item])]), self.variant(100, [(12, []), (35, [])])])
        self.assertEqual(1, len(result["objects"]))
        scale = result["objects"][0]["scales"][1]
        self.assertIsNone(scale["first_observed_seconds"])
        self.assertEqual(35, scale["last_not_observed_before_first_seconds"])
        self.assertEqual("not_observed", scale["publication_state"])
        self.assertEqual([12, 35], [sample["seconds"] for sample in scale["observations"]])

    def test_fully_inside_objects_are_not_far_publication_records(self):
        result = scene_scale.crossing_observations([
            self.variant(1, [(10, [{**self.item, "category": "fully_inside"}])])])
        self.assertEqual([], result["objects"])

    def test_coincident_objects_keep_all_ids_and_geometry_records(self):
        result = scene_scale.crossing_observations([
            self.variant(1, [(10, [self.item, {**self.item, "id": {"Value": 2}}])])])
        self.assertEqual(1, len(result["objects"]))
        observed = result["objects"][0]["scales"][0]["observations"][0]["objects"]
        self.assertEqual([1, 2], [item["id"]["Value"] for item in observed])

    def test_distinct_world_poses_or_bounds_remain_distinct_spatial_records(self):
        for field, value in (("worldPose", {"Position": {"X": 1, "Y": 0, "Z": 0}}),
                             ("worldBounds", {"Minimum": {"X": 0}, "Maximum": {"X": 128}})):
            with self.subTest(field=field):
                result = scene_scale.crossing_observations([
                    self.variant(1, [(10, [self.item, {**self.item, field: value}])])])
                self.assertEqual(2, len(result["objects"]))


class LocalValidationTests(unittest.TestCase):
    def setUp(self):
        self.baseline = {"heightMissing": 0, "maximumRepeatedHeightDifference": 0,
                         "triangleWorldCoordinatesMaterialSha256": "geometry",
                         "terrainHeightSha256": "height", "railSteelSha256": "rail",
                         "completed": True, "bounded_stability": True, "stability_verified": True,
                         "nonRailTriangleWorldCoordinatesMaterialSha256": "other",
                         "railCenterline": {"parkingOutside": True, "sha256": "centerline"},
                         "triangles": 100, "railSteelTriangles": 20,
                         "stability_samples": [{"cameraStage": stage, "firstGeometry": {
                             "fully_inside": {"objects": 1, "nonRailTriangles": 80,
                                              "nonRailTriangleWorldCoordinatesMaterialSha256": "near"},
                             "crossing_boundary": {"nonRailTriangles": 20,
                                                   "nonRailTriangleWorldCoordinatesMaterialSha256": "far"}}}
                             for stage in range(5)]}

    def test_stable_complete_near_scene_is_accepted(self):
        self.assertEqual({"equal": True, "triangle_delta": 0, "rail_steel_triangle_delta": 0},
                         scene_scale.validate_local(self.baseline, {**self.baseline, "stability_verified": True}))

    def test_empty_fully_inside_inventory_cannot_pass_on_matching_empty_hashes(self):
        empty = copy.deepcopy(self.baseline)
        for sample in empty["stability_samples"]:
            sample["firstGeometry"]["fully_inside"]["objects"] = 0
        with self.assertRaisesRegex(ValueError, "near_scene_objects_missing"):
            scene_scale.validate_local(empty, empty)

    def test_additional_large_scale_slots_must_pass_the_near_exclusion(self):
        with self.assertRaisesRegex(ValueError, "additional_slots_unavailable"):
            scene_scale.chosen_slots([(0, 2)], 100)
        self.assertEqual([(0, 2), (0, 3)], scene_scale.chosen_slots([(0, 2), (0, 3)], 100))

    def test_required_stability_check_cannot_be_omitted_or_coerced(self):
        for value in (None, False, 1, "true"):
            with self.subTest(value=value):
                with self.assertRaisesRegex(ValueError, "stability_check_not_verified"):
                    scene_scale.validate_local(self.baseline, {**self.baseline, "stability_verified": value})

    def test_every_hash_and_full_height_coverage_are_required(self):
        for field, value in (("heightMissing", 1), ("maximumRepeatedHeightDifference", .001),
                             ("terrainHeightSha256", "different")):
            with self.subTest(field=field):
                with self.assertRaisesRegex(ValueError, "near_scene_geometry_changed"):
                    scene_scale.validate_local(self.baseline, {**self.baseline, field: value})

    def test_each_camera_keeps_identical_fully_inside_nonrail_geometry(self):
        for stage in range(5):
            for field, value in (("nonRailTriangles", 79),
                                 ("nonRailTriangleWorldCoordinatesMaterialSha256", "changed")):
                with self.subTest(stage=stage, field=field):
                    changed = copy.deepcopy(self.baseline)
                    changed["stability_samples"][stage]["firstGeometry"]["fully_inside"][field] = value
                    with self.assertRaisesRegex(ValueError, "near_scene_geometry_changed"):
                        scene_scale.validate_local(self.baseline, changed)

    def test_crossing_far_geometry_can_differ_without_changing_near_acceptance(self):
        changed = copy.deepcopy(self.baseline)
        changed["nonRailTriangleWorldCoordinatesMaterialSha256"] = "far-not-published"
        changed["stability_samples"][4]["firstGeometry"]["crossing_boundary"] = {
            "nonRailTriangles": 0, "nonRailTriangleWorldCoordinatesMaterialSha256": "empty"}
        scene_scale.validate_local(self.baseline, changed)

    def test_missing_or_reordered_camera_samples_cannot_skip_near_checks(self):
        for stages in ((0, 1, 2, 3), (0, 1, 2, 3, 3), (0, 1, 3, 2, 4)):
            with self.subTest(stages=stages):
                changed = copy.deepcopy(self.baseline)
                changed["stability_samples"] = [changed["stability_samples"][i] for i in stages]
                with self.assertRaisesRegex(ValueError, "local_observation_incomplete"):
                    scene_scale.validate_local(self.baseline, changed)

    def test_unknown_or_unstable_local_observations_cannot_be_accepted_at_any_scale(self):
        for field in ("completed", "bounded_stability"):
            for value in (None, False, 1, "true"):
                with self.subTest(field=field, value=value):
                    with self.assertRaisesRegex(ValueError, "local_observation_incomplete"):
                        scene_scale.validate_local(self.baseline, {**self.baseline, field: value})

    def test_mesh_segmentation_drift_keeps_layout_validation_and_reports_raw_deltas(self):
        changed = {**self.baseline, "triangleWorldCoordinatesMaterialSha256": "different-geometry",
                   "railSteelSha256": "different-rail-mesh", "triangles": 90, "railSteelTriangles": 14}
        result = scene_scale.validate_local(self.baseline, changed)
        self.assertEqual({"equal": False, "triangle_delta": -10, "rail_steel_triangle_delta": -6}, result)
        sleepers = {**self.baseline, "triangleWorldCoordinatesMaterialSha256": "different-sleepers", "triangles": 98}
        self.assertEqual({"equal": False, "triangle_delta": -2, "rail_steel_triangle_delta": 0},
                         scene_scale.validate_local(self.baseline, sleepers))

    def test_railway_layout_change_still_stops_the_scale_point(self):
        changed = {**self.baseline, "railCenterline": {"parkingOutside": True, "sha256": "changed"}}
        with self.assertRaisesRegex(ValueError, "near_railway_layout_changed"):
            scene_scale.validate_local(self.baseline, changed)

    def test_unobserved_parking_tracks_cannot_be_silently_accepted(self):
        for value in (None, False, 1, "true"):
            with self.subTest(value=value):
                changed = {**self.baseline, "railCenterline": {"parkingOutside": value, "sha256": "centerline"}}
                with self.assertRaisesRegex(ValueError, "near_railway_coverage_unproven"):
                    scene_scale.validate_local(self.baseline, changed)


if __name__ == "__main__":
    unittest.main()
