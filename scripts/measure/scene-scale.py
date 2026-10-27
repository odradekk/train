"""用 #499 的布局配方生成四档普通存档及公开 N/C/F 核对结果。"""
# Decision note: 2026-10-26-post-demo-performance-and-memory-gaps

import argparse
import copy
import hashlib
import json
import math
import shutil
import subprocess
from pathlib import Path


def validate_variant(baseline, stats, bounds, focus, multiple):
    """固定近景和默认焦点，只扩大远处建筑；失配时拒绝该档。"""
    if any(stats[field] != baseline[field] for field in ("near96", "groundNear96")):
        raise ValueError("near_content_changed")
    if stats["far"]["N"] < multiple * baseline["far"]["N"]:
        raise ValueError("far_growth_insufficient")
    if any(abs(bounds["focus"][axis] - focus[axis]) >= .001 for axis in range(2)):
        raise ValueError("focus_calibration_changed")
    if stats["businessObjectsWithin32m"] <= 0:
        raise ValueError("near_business_missing")


def validate_local(baseline, local, verify_stability):
    """逐相机比较圆内非轨道几何；跨界远处几何只记录，不参与跨档判定。"""
    if local.get("completed") is not True or local.get("bounded_stability") is not True:
        raise ValueError("local_observation_incomplete")
    if any([sample["cameraStage"] for sample in value["stability_samples"]] != list(range(5))
           for value in (baseline, local)):
        raise ValueError("local_observation_incomplete")
    if local["heightMissing"] != 0 or local["maximumRepeatedHeightDifference"] != 0 or \
            local["terrainHeightSha256"] != baseline["terrainHeightSha256"] or any(
                sample["firstGeometry"]["fully_inside"][field] != reference["firstGeometry"]["fully_inside"][field]
                for reference, sample in zip(baseline["stability_samples"], local["stability_samples"])
                for field in ("nonRailTriangles", "nonRailTriangleWorldCoordinatesMaterialSha256")):
        raise ValueError("near_scene_geometry_changed")
    if local["railCenterline"]["parkingOutside"] is not True:
        raise ValueError("near_railway_coverage_unproven")
    if local["railCenterline"]["sha256"] != baseline["railCenterline"]["sha256"]:
        raise ValueError("near_railway_layout_changed")
    if verify_stability and local.get("stability_verified") is not True:
        raise ValueError("stability_check_not_verified")
    return {"equal": all(local[field] == baseline[field] for field in (
                "triangleWorldCoordinatesMaterialSha256", "railSteelSha256")),
            "triangle_delta": local["triangles"] - baseline["triangles"],
            "rail_steel_triangle_delta": local["railSteelTriangles"] - baseline["railSteelTriangles"]}


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def crossing_observations(variants):
    """按空间范围合并跨界对象，保留快照观测时刻，不推断精确发布时间。"""
    spaces, runs = {}, []
    for variant in variants:
        snapshots = []
        for sample in variant["local96"]["stability_samples"]:
            for phase in ("first", "second"):
                if sample.get(phase) is None:
                    continue
                groups = {}
                for item in sample[phase + "Geometry"]["objects"]:
                    if item["category"] != "crossing_boundary":
                        continue
                    space = {field: item[field] for field in ("worldPose", "worldBounds")}
                    key = json.dumps(space, sort_keys=True, separators=(",", ":"))
                    spaces[key] = space
                    groups.setdefault(key, []).append({field: item[field] for field in (
                        "id", "localTriangles", "triangleWorldCoordinatesMaterialSha256",
                        "nonRailTriangles", "nonRailTriangleWorldCoordinatesMaterialSha256")})
                snapshots.append((sample[phase]["seconds"], groups))
        runs.append((variant["multiple"], sorted(snapshots, key=lambda snapshot: snapshot[0])))
    objects = []
    for key, space in sorted(spaces.items()):
        scales = []
        for multiple, snapshots in runs:
            observations = [{"seconds": seconds, "objects": groups.get(key, [])} for seconds, groups in snapshots]
            first = next((sample["seconds"] for sample in observations if sample["objects"]), None)
            absent = [sample["seconds"] for sample in observations if not sample["objects"] and
                      (first is None or sample["seconds"] < first)]
            scales.append({"multiple": multiple, "first_observed_seconds": first,
                           "last_not_observed_before_first_seconds": max(absent, default=None),
                           "publication_state": "not_observed" if first is None else "published_geometry_observed",
                           "observations": observations})
        objects.append({**space, "scales": scales})
    return {"method": "Snapshot observation times, not exact publication times. First observation bounds publication from above. "
            "Missing local geometry does not prove non-publication: the inventory omits objects without intersecting triangles. "
            "The probe is not the game frame loop; game fill-in is measured by far_ready.", "objects": objects}


def write(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, separators=(",", ":"), allow_nan=False) + "\n", encoding="utf-8")


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def plot_rect(plot):
    # #499 的放置算法使用地块包围盒；LargeStation 是当前内容新增的尺寸。
    sizes = {"small": (48, 48), "medium": (96, 96), "large": (192, 192), "giant": (384, 384),
             "small_station": (192, 112), "large_station": (256, 208)}
    width, depth = sizes[plot["size"]]
    x, _, z = plot["at"]
    return x - width // 2, x + width // 2 - 1, z - depth // 2, z + depth // 2 - 1


def pair(i, j):
    return [{"id": 0, "size": "giant", "at": [x + 896 * i, 1, 384 + 512 * j],
             "orientation": "px_py", "building": building}
            for x, building in ((-224, "core-city-hub"), (224, "bastion-fortress"))]


def slots_for(template, focus):
    original = [plot_rect(plot) for plot in template["plots"]]

    def valid(plots):
        for plot in plots:
            left, right, top, bottom = plot_rect(plot)
            if any(left <= b and right >= a and top <= d and bottom >= c for a, b, c, d in original):
                return False
            dx, dz = max(left - focus[0], 0, focus[0] - right), max(top - focus[1], 0, focus[1] - bottom)
            if dx * dx + dz * dz <= 192 ** 2:
                return False
        return True

    slots = [(i, j) for i in range(-12, 13) for j in range(-15, 16)
             if (i > 0 or i == 0 and j > 0) and valid(pair(i, j)) and valid(pair(-i, -j))]
    slots.sort(key=lambda item: (max(abs(item[0] * 896) + 416, abs(item[1] * 512) + 192),
                                 item[0] ** 2 + item[1] ** 2, item))
    return slots


def generate(root, output, configuration):
    manifest = {"stage": "build", "completed": False, "variants": [],
                "definition": "N: building projection voxels outside radius192 cells; C: unique containing world32^3 chunks; F: exposed six-neighbor faces owned by far cells. Authored ground is separate; procedural terrain N/C/F is unavailable."}
    manifest_path = output / "scale-manifest.json"

    def command(arguments, name, timeout, json_output=False):
        def log(text):
            for directory, label in ((output, "<run>"), (root, "<source>")):
                text = text.replace(str(directory), label).replace(directory.as_posix(), label)
            (output / (name + ".log")).write_text(text, encoding="utf-8")

        try:
            result = subprocess.run(list(map(str, arguments)), cwd=root, capture_output=True,
                                    text=True, encoding="utf-8", errors="replace", timeout=timeout)
        except subprocess.TimeoutExpired as error:
            log((error.stdout or b"").decode("utf-8", errors="replace") +
                (error.stderr or b"").decode("utf-8", errors="replace"))
            raise
        log(result.stdout + result.stderr)
        value = None
        if json_output and result.stdout.strip():
            try:
                value = json.loads(result.stdout.strip().splitlines()[-1])
            except json.JSONDecodeError:
                if not result.returncode:
                    raise
            else:
                write(output / (name + ".json"), value)
        if result.returncode:
            if value is not None:
                manifest["failed_observation"] = name + ".json"
            raise RuntimeError(name + "_failed")
        return value if json_output else result.stdout

    probe = root / f"Tools/SceneScale/bin/{configuration}/net10.0/Train.SceneScale.dll"
    cli = root / f"Tools/Cli/bin/{configuration}/net10.0/Train.Cli.dll"

    def observe(repository, mode, save, name, verify_stability=False):
        arguments = ["dotnet", probe, repository, mode, save]
        if mode in ("stats", "local"):
            arguments.append(output / "focus.json")
        if verify_stability:
            arguments.append("--verify-stable")
        # 五个位置各有 300 秒独立上限；额外时间留给加载和进程退出。
        return command(arguments, name, 1620 if mode == "local" else 360, json_output=True)

    try:
        for project in ("Cli", "SceneScale"):
            command(["dotnet", "build", root / f"Tools/{project}/Train.{project}.csproj",
                     "-c", configuration, "--nologo"], "build-" + project.lower(), 300)
        manifest["source_head"] = command(["git", "rev-parse", "HEAD"], "source-head", 30).strip()
        manifest["source_dirty"] = bool(command(["git", "status", "--porcelain"], "source-status", 30).strip())
        manifest["configuration"] = configuration
        manifest["sources_sha256"] = {path.relative_to(root).as_posix(): sha(path)
            for path in sorted([Path(__file__).resolve(), *root.glob("Tools/SceneScale/*.cs"),
                                root / "Tools/SceneScale/Train.SceneScale.csproj"])}
        manifest["builtin_sha256"] = {path.relative_to(root / "builtin").as_posix(): sha(path)
                                      for path in sorted((root / "builtin").rglob("*")) if path.is_file()}
        manifest["assemblies_sha256"] = {path.name: sha(path) for path in sorted(probe.parent.glob("Train.*.dll"))}
        manifest["stage"] = "native"
        native_save = root / "examples/starting-trade/save.json"
        observe(root, "bounds", native_save, "native-bounds")
        focus = [0, 0]
        write(output / "focus.json", focus)
        baseline = observe(root, "stats", native_save, "native-stats")
        manifest["native_save_sha256"] = sha(native_save)
        manifest["focus_cells"] = [focus[0], 0, focus[1]]
        manifest["focus_rule"] = "Current node initial view: node world origin; pan-bounds center is navigation metadata only."
        manifest["baseline_far"] = baseline["far"]
        pair_n = sum(item["totalN"] for item in baseline["buildingObjects"] if item["plot"] in (11, 12))
        if pair_n <= 0 or baseline["far"]["N"] <= 0:
            raise ValueError("baseline_far_content_missing")
        template_relative = Path("builtin/core/resources/settlement-templates/core-city.settlement-template.json")
        ground_relative = Path("builtin/core/resources/blueprints/core-city-ground.blueprint.json")
        template, ground = read(root / template_relative), read(root / ground_relative)
        slots = slots_for(template, focus)
        recipe = read(root / "examples/starting-trade/recipe.json")
        for sequence, entry in enumerate(recipe["commands"]):
            entry["at"] = {"completed_steps": 0, "sequence": sequence}
        recipe["target"], recipe["checkpoint_steps"] = {"completed_steps": 1, "sequence": 0}, []
        write(output / "recipe.json", recipe)
        manifest["recipe_sha256"] = sha(output / "recipe.json")
        expected_trains = {entry["train"] for entry in recipe["commands"] if entry["kind"] == "train.start"}
        (output / "saves").mkdir()
        local_baseline = None
        for multiple in (1, 10, 30, 100):
            manifest["stage"] = f"{multiple}x-input"
            repository = output / f"inputs/{multiple}x"
            repository.mkdir(parents=True)
            shutil.copytree(root / "builtin", repository / "builtin")
            shutil.copy2(root / "project.godot", repository / "project.godot")
            variant_template, variant_ground = copy.deepcopy(template), copy.deepcopy(ground)
            pairs = 0 if multiple == 1 else math.ceil((multiple - 1) * baseline["far"]["N"] / pair_n)
            pairs += pairs % 2
            chosen = slots
            if multiple == 100:
                available = set(slots)
                chosen = [(i, j) for i in range(1, 10) for j in range(-3, 4) if (i, j) in available]
                chosen.sort(key=lambda item: (abs(item[0]), abs(item[1]), item))
                # 焦点改到原点后，近景圆排除的位置变了；保留原 63 个位置，补足两个有效位置。
                chosen.extend([(0, 2), (0, 3)])
            if pairs // 2 > len(chosen):
                raise ValueError("archived_slots_insufficient")
            for i, j in chosen[:pairs // 2]:
                for plots in (pair(i, j), pair(-i, -j)):
                    for plot in plots:
                        plot["id"] = len(variant_template["plots"]) + 1
                        variant_template["plots"].append(plot)
            if multiple != 1:
                # 沿用 #499 的 flatten 方法；焦点改到原点后，近景圆排除的位置变了，
                # 30×、100× 按批准值扩边以容纳远处地块，核对失配时仍停止。
                lower, upper = {10: ([-1312, -1216], [1983, 1599]),
                                30: ([-2208, -1856], [3327, 2623]),
                                100: ([-8480, -1728], [8703, 2111])}[multiple]
                variant_ground["appearance"]["flatten"] = {"from": lower, "to": upper, "height": 1}
                write(repository / template_relative, variant_template)
                write(repository / ground_relative, variant_ground)
            bounds = observe(repository, "bounds", "-", f"{multiple}x-bounds")
            save = output / f"saves/{multiple}x.save.json"
            command(["dotnet", cli, "make", repository, output / "recipe.json", save], f"{multiple}x-make", 180)
            stats = observe(root, "stats", save, f"{multiple}x-stats")
            restored_bounds = observe(root, "bounds", save, f"{multiple}x-restored-bounds")
            variant = {"multiple": multiple, "extra_pairs": pairs, "save": save.relative_to(output).as_posix(),
                       "save_sha256": sha(save), "template_sha256": sha(repository / template_relative),
                       "ground_sha256": sha(repository / ground_relative), "far": stats["far"],
                       "far_N_multiple": stats["far"]["N"] / baseline["far"]["N"],
                       "focus_error_cells": [bounds["focus"][axis] - focus[axis] for axis in range(2)]}
            manifest["variants"].append(variant)
            write(manifest_path, manifest)
            for current_bounds in (bounds, restored_bounds):
                validate_variant(baseline, stats, current_bounds, focus, multiple)
            trains = {item["Id"]["Value"]: item for item in stats["trainStates"]}
            if any(train not in trains or trains[train]["Activity"] != 1 or not trains[train]["loop"] or
                   not trains[train]["planStops"] for train in expected_trains):
                raise ValueError("recipe_train_load_changed")
            manifest["stage"] = f"{multiple}x-local"
            ready = observe(root, "ready", save, f"{multiple}x-ready")
            if ready["businessSceneRootsWithin32m"] <= 0:
                raise ValueError("near_scene_business_missing")
            verify_stability = multiple in (1, 100)
            local = observe(root, "local", save, f"{multiple}x-local96", verify_stability)
            variant["local96"] = local
            write(manifest_path, manifest)
            local_baseline = local_baseline or local
            variant["raw_mesh_comparison"] = validate_local(local_baseline, local, verify_stability)
            variant["near_content_match"] = variant["local_content_match"] = True
            write(manifest_path, manifest)
            print(json.dumps({"multiple": multiple, "far": stats["far"], "near_match": True}), flush=True)
        manifest["crossing_geometry_observations"] = crossing_observations(manifest["variants"])
        manifest["stage"], manifest["completed"] = "completed", True
        write(manifest_path, manifest)
    except Exception as error:
        manifest["error_type"] = type(error).__name__
        if type(error) is ValueError:
            manifest["failure"] = str(error)
        write(manifest_path, manifest)
        raise


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--configuration", choices=("Debug", "Release"), default="Release")
    args = parser.parse_args()
    root, output = args.root.resolve(), args.output.resolve()
    if output.exists() and any(output.iterdir()):
        parser.error("output_directory_must_be_new_or_empty")
    output.mkdir(parents=True, exist_ok=True)
    try:
        generate(root, output, args.configuration)
    except Exception as error:
        print("scene_scale_failed: " + type(error).__name__ + "; see scale-manifest.json")
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
