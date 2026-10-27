"""生成四档普通存档和 N/C/F，另在本机核对可打开的两档近景。"""
# Decision note: 2026-10-26-post-demo-performance-and-memory-gaps

import argparse
import copy
import json
import math
import shutil
from pathlib import Path

from scene_scale_run import ScaleRun, input_identity, read, sha, write


def validate_variant(baseline, stats, multiple):
    """固定原点近景，只扩大远处建筑；失配时拒绝该档。"""
    if any(stats[field] != baseline[field] for field in ("near96", "groundNear96")):
        raise ValueError("near_content_changed")
    if stats["far"]["N"] < multiple * baseline["far"]["N"]:
        raise ValueError("far_growth_insufficient")
    if stats["businessObjectsWithin32m"] <= 0:
        raise ValueError("near_business_missing")


def validate_local(baseline, local, verify_stability):
    """逐相机比较圆内非轨道几何；跨界远处几何只记录，不参与跨档判定。"""
    if local.get("completed") is not True or local.get("bounded_stability") is not True:
        raise ValueError("local_observation_incomplete")
    if any([sample["cameraStage"] for sample in value["stability_samples"]] != list(range(5))
           for value in (baseline, local)):
        raise ValueError("local_observation_incomplete")
    if any(sample["firstGeometry"]["fully_inside"]["objects"] <= 0
           for value in (baseline, local) for sample in value["stability_samples"]):
        raise ValueError("near_scene_objects_missing")
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



def plot_rect(plot):
    # 放置筛选使用完整地块包围盒，不能只排除建筑中心。
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


def chosen_slots(slots, multiple):
    if multiple != 100:
        return slots
    available = set(slots)
    extra = [(0, 2), (0, 3)]
    if not all(item in available for item in extra):
        raise ValueError("additional_slots_unavailable")
    chosen = [(i, j) for i in range(1, 10) for j in range(-3, 4) if (i, j) in available]
    chosen.sort(key=lambda item: (abs(item[0]), abs(item[1]), item))
    # 焦点在原点时近景圆排除的位置变了；保留原 63 个位置，补足两个有效位置。
    return chosen + extra


def generate(run):
    root, output, manifest = run.root, run.output, run.manifest
    manifest["definition"] = "N: building projection voxels outside radius192 cells; C: unique containing world32^3 chunks; F: exposed six-neighbor faces owned by far cells. Authored ground is separate; procedural terrain N/C/F is unavailable."
    run.prepare()
    native_save = root / "examples/starting-trade/save.json"
    run.observe(root, "bounds", native_save, "native-bounds")
    baseline = run.observe(root, "stats", native_save, "native-stats")
    manifest.update({"native_save_sha256": sha(native_save), "focus_cells": [0, 0, 0],
                     "focus_rule": "Node world origin; pan-bounds center is navigation metadata only.",
                     "baseline_far": baseline["far"]})
    pair_n = sum(item["totalN"] for item in baseline["buildingObjects"] if item["plot"] in (11, 12))
    if pair_n <= 0 or baseline["far"]["N"] <= 0:
        raise ValueError("baseline_far_content_missing")
    template_relative = Path("builtin/core/resources/settlement-templates/core-city.settlement-template.json")
    ground_relative = Path("builtin/core/resources/blueprints/core-city-ground.blueprint.json")
    template, ground = read(root / template_relative), read(root / ground_relative)
    slots = slots_for(template, [0, 0])
    recipe = read(root / "examples/starting-trade/recipe.json")
    for sequence, entry in enumerate(recipe["commands"]):
        entry["at"] = {"completed_steps": 0, "sequence": sequence}
    recipe["target"], recipe["checkpoint_steps"] = {"completed_steps": 1, "sequence": 0}, []
    write(output / "recipe.json", recipe)
    manifest["recipe_sha256"] = sha(output / "recipe.json")
    expected_trains = {entry["train"] for entry in recipe["commands"] if entry["kind"] == "train.start"}
    (output / "saves").mkdir()
    for multiple in (1, 10, 30, 100):
        manifest["stage"] = f"{multiple}x-input"
        repository = output / f"inputs/{multiple}x"
        repository.mkdir(parents=True)
        shutil.copytree(root / "builtin", repository / "builtin")
        shutil.copy2(root / "project.godot", repository / "project.godot")
        variant_template, variant_ground = copy.deepcopy(template), copy.deepcopy(ground)
        pairs = 0 if multiple == 1 else math.ceil((multiple - 1) * baseline["far"]["N"] / pair_n)
        pairs += pairs % 2
        chosen = chosen_slots(slots, multiple)
        if pairs // 2 > len(chosen):
            raise ValueError("archived_slots_insufficient")
        for i, j in chosen[:pairs // 2]:
            for plots in (pair(i, j), pair(-i, -j)):
                for plot in plots:
                    plot["id"] = len(variant_template["plots"]) + 1
                    variant_template["plots"].append(plot)
        if multiple != 1:
            # 保持 flatten 方法；原点近景圆排除的位置变化后，需要扩边容纳远处地块。
            lower, upper = {10: ([-1312, -1216], [1983, 1599]),
                            30: ([-2208, -1856], [3327, 2623]),
                            100: ([-8480, -1728], [8703, 2111])}[multiple]
            variant_ground["appearance"]["flatten"] = {"from": lower, "to": upper, "height": 1}
            write(repository / template_relative, variant_template)
            write(repository / ground_relative, variant_ground)
        bounds = run.observe(repository, "bounds", "-", f"{multiple}x-bounds")
        save = output / f"saves/{multiple}x.save.json"
        run.command(["dotnet", run.cli, "make", repository, output / "recipe.json", save], f"{multiple}x-make", 180)
        stats = run.observe(root, "stats", save, f"{multiple}x-stats")
        run.observe(root, "bounds", save, f"{multiple}x-restored-bounds")
        variant = {"multiple": multiple, "extra_pairs": pairs, "save": save.relative_to(output).as_posix(),
                   "template_sha256": sha(repository / template_relative),
                   "ground_sha256": sha(repository / ground_relative), "far": stats["far"],
                   "far_N_multiple": stats["far"]["N"] / baseline["far"]["N"],
                   "navigation_center": bounds["navigationCenter"],
                   "input_identity": input_identity(output, {"multiple": multiple, "save": save.relative_to(output).as_posix()})}
        manifest["variants"].append(variant)
        run.save()
        validate_variant(baseline, stats, multiple)
        trains = {item["Id"]["Value"]: item for item in stats["trainStates"]}
        if any(train not in trains or trains[train]["Activity"] != "Running" or not trains[train]["loop"] or
               not trains[train]["planStops"] for train in expected_trains):
            raise ValueError("recipe_train_load_changed")
        variant["near_content_match"] = True
        run.save()
        print(json.dumps({"multiple": multiple, "far": stats["far"], "near_match": True}), flush=True)


def verify(run, inputs):
    generated = read(inputs / "scale-manifest.json")
    if generated.get("completed") is not True or generated.get("operation") != "generate" or \
            [item["multiple"] for item in generated["variants"]] != [1, 10, 30, 100]:
        raise ValueError("generation_incomplete")
    if sha(inputs / "recipe.json") != generated["recipe_sha256"]:
        raise ValueError("recipe_identity_changed")
    # 校验四档实际文件，绑定清单；近景探针只运行可在测试机打开的两档。
    for variant in generated["variants"]:
        if input_identity(inputs, variant) != variant["input_identity"]:
            raise ValueError("input_identity_changed")
    run.prepare()
    if any(generated[field] != run.manifest[field] for field in
           ("source_head", "builtin_identity", "configuration")):
        raise ValueError("generation_source_changed")
    run.manifest["generation_manifest_sha256"] = sha(inputs / "scale-manifest.json")
    baseline = None
    for original in generated["variants"][:2]:
        multiple = original["multiple"]
        variant = {"multiple": multiple, "input_identity": original["input_identity"]}
        run.manifest["variants"].append(variant)
        ready = run.observe(run.root, "ready", inputs / original["save"], f"{multiple}x-ready")
        if ready["businessSceneRootsWithin32m"] <= 0:
            raise ValueError("near_scene_business_missing")
        local = run.observe(run.root, "local", inputs / original["save"], f"{multiple}x-local96", True)
        variant["local96"] = local
        run.save()
        baseline = baseline or local
        variant["raw_mesh_comparison"] = validate_local(baseline, local, True)
        variant["local_content_match"] = True
        run.save()
        print(json.dumps({"multiple": multiple, "local_match": True}), flush=True)
    run.manifest["crossing_geometry_observations"] = crossing_observations(run.manifest["variants"])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="operation", required=True)
    for operation in ("generate", "verify"):
        subparser = subparsers.add_parser(operation)
        subparser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[2])
        subparser.add_argument("--output", type=Path, required=True)
        subparser.add_argument("--configuration", choices=("Debug", "Release"), default="Release")
        if operation == "verify":
            subparser.add_argument("--input", type=Path, required=True)
    args = parser.parse_args()
    output = args.output.resolve()
    if output.exists() and any(output.iterdir()):
        parser.error("output_directory_must_be_new_or_empty")
    output.mkdir(parents=True, exist_ok=True)
    run = ScaleRun(args.root.resolve(), output, args.configuration, args.operation)
    try:
        if args.operation == "generate":
            generate(run)
        else:
            verify(run, args.input.resolve())
        run.manifest["stage"], run.manifest["completed"] = "completed", True
        run.save()
    except Exception as error:
        run.manifest["error_type"] = type(error).__name__
        if type(error) in (ValueError, RuntimeError):
            run.manifest["failure"] = str(error)
        run.save()
        print("scene_scale_failed: " + type(error).__name__ + "; see " + run.manifest_path.name)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
