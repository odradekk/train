"""Compile the probe's pure observation logic; real scene wiring is checked separately."""
import subprocess
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]

from scene_scale_probe_fixture import HARNESS
from scene_scale_probe_nonrail_fixture import NONRAIL_CASES

HARNESS += "\n" + NONRAIL_CASES


class ProbeObservationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temporary = tempfile.TemporaryDirectory(prefix="train-probe-tests-")
        folder = Path(cls.temporary.name)
        cls.project = folder / "ProbeTests.csproj"
        references = "".join(f'<ProjectReference Include="{ROOT / "Contracts" / name / ("Train." + name + ".Contracts.csproj")}" />'
                             for name in ("Core", "Content", "Scenes"))
        sources = "".join(f'<Compile Include="{path}" />'
                          for path in sorted((ROOT / "Tools/SceneScale").glob("Probe.Local*.cs")))
        cls.project.write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>'
                              '<OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>'
                              '<ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>'
                              '</PropertyGroup><ItemGroup>' + references + sources + '</ItemGroup></Project>', encoding="utf-8")
        (folder / "Program.cs").write_text(HARNESS, encoding="utf-8")
        built = subprocess.run(["dotnet", "build", cls.project, "--nologo"], cwd=ROOT,
                               capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=120)
        if built.returncode:
            raise AssertionError(built.stdout + built.stderr)
        cls.assembly = folder / "bin/Debug/net10.0/ProbeTests.dll"

    @classmethod
    def tearDownClass(cls):
        cls.temporary.cleanup()

    def check_case(self, name):
        result = subprocess.run(["dotnet", self.assembly, name], cwd=ROOT, capture_output=True,
                                text=True, encoding="utf-8", errors="replace", timeout=20)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)

    def test_all_original_exact_height_columns_complete_without_a_global_wait(self):
        self.check_case("complete")

    def test_only_ready_frames_complete(self):
        self.check_case("readiness")

    def test_a_single_missing_precise_column_blocks_completion(self):
        self.check_case("missing")

    def test_fog_boxes_intersect_the_fixed_circle_in_world_coordinates(self):
        self.check_case("fog")

    def test_stable_resampling_waits_ten_seconds(self):
        self.check_case("repeat-time")

    def test_any_hash_or_coverage_drift_fails_stability(self):
        self.check_case("drift")

    def test_geometry_and_object_set_need_thirty_stable_seconds_with_a_position_deadline(self):
        self.check_case("stability-window")

    def test_snapshot_updates_follow_full_replacement_and_root_shift_semantics(self):
        self.check_case("updates")

    def test_full_local_capture_keeps_geometry_material_quantization_and_exact_height_hash(self):
        self.check_case("capture")

    def test_local_geometry_cache_tracks_mesh_pose_focus_and_evicts_removed_objects(self):
        self.check_case("geometry-cache")

    def test_nonrail_geometry_keeps_every_other_material_in_mixed_objects(self):
        self.check_case("nonrail")


class ProbeCommandTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        built = subprocess.run(["dotnet", "build", ROOT / "Tools/SceneScale/Train.SceneScale.csproj", "--nologo"],
                               cwd=ROOT, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=120)
        if built.returncode:
            raise AssertionError(built.stdout + built.stderr)
        cls.assembly = ROOT / "Tools/SceneScale/bin/Debug/net10.0/Train.SceneScale.dll"

    def test_stability_flag_is_accepted_only_after_local_focus(self):
        with tempfile.TemporaryDirectory(prefix="train-probe-cli-") as temporary:
            folder = Path(temporary)
            focus = folder / "focus.json"
            focus.write_text("[0,0]", encoding="utf-8")
            for mode, optional, expected in (
                ("local", [str(focus), "--verify-stable"], "FileNotFoundException"),
                ("local", [str(focus)], "FileNotFoundException"),
                ("local", ["--verify-stable"], "ArgumentException"),
                ("ready", ["--verify-stable"], "ArgumentException"),
                ("stats", [str(focus), "--verify-stable"], "ArgumentException"),
                ("bounds", ["--verify-stable"], "ArgumentException"),
                ("local", [str(focus), "--verify-stable", "extra"], "ArgumentException"),
            ):
                with self.subTest(mode=mode, optional=optional):
                    result = subprocess.run(["dotnet", self.assembly, folder, mode, "private-save.json", *optional],
                                            cwd=ROOT, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=20)
                    self.assertNotEqual(0, result.returncode)
                    self.assertEqual("", result.stdout)
                    self.assertIn("scene-scale failed: " + expected, result.stderr)
                    self.assertNotIn(str(folder), result.stderr)
                    self.assertNotIn("private-save", result.stderr)


if __name__ == "__main__":
    unittest.main()
