"""用 JSON Schema 校验 docs/content/examples 下的全部示例文件。

只检查单个文件的结构和取值范围；引用、连通、包络等语义规则由加载器检查。
需要 Python 包 jsonschema（会一并装上 referencing）：pip install jsonschema

用法：python validate_examples.py [docs/content 目录]，默认是本脚本的上一级目录。
"""
import glob
import json
import os
import sys

from jsonschema import Draft202012Validator
from referencing import Registry, Resource

SCHEMA_BY_SUFFIX = {
    ".voxel.json": "voxel.schema.json",
    ".blueprint.json": "blueprint.schema.json",
    ".settlement-type.json": "settlement-type.schema.json",
    ".settlement-template.json": "settlement-template.schema.json",
    ".market-table.json": "market-table.schema.json",
    ".process.json": "process.schema.json",
    ".population-rule.json": "population-rule.schema.json",
    ".market-rule.json": "market-rule.schema.json",
    ".construction-rule.json": "construction-rule.schema.json",
    ".railway-rule.json": "railway-rule.schema.json",
    ".combat-rule.json": "combat-rule.schema.json",
    ".lair-type.json": "lair-type.schema.json",
    ".generation-profile.json": "generation-profile.schema.json",
    ".terrain-type.json": "terrain-type.schema.json",
}


def load(path):
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def main():
    root = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
    schemas = {}
    for path in glob.glob(os.path.join(root, "schemas", "*.schema.json")):
        schema = load(path)
        Draft202012Validator.check_schema(schema)
        schemas[os.path.basename(path)] = schema
    registry = Registry().with_resources([(s["$id"], Resource.from_contents(s)) for s in schemas.values()])

    files = errors = 0
    for path in sorted(glob.glob(os.path.join(root, "examples", "**", "*.json"), recursive=True)):
        name = os.path.basename(path)
        if name == "mod.json":
            schema = "manifest.schema.json"
        else:
            schema = next((s for suffix, s in SCHEMA_BY_SUFFIX.items() if name.endswith(suffix)), None)
            if schema is None:
                print(f"未知的文件种类：{os.path.relpath(path, root)}")
                errors += 1
                continue
        files += 1
        validator = Draft202012Validator(schemas[schema], registry=registry)
        for error in validator.iter_errors(load(path)):
            errors += 1
            print(f"{os.path.relpath(path, root)} {list(error.absolute_path)}: {error.message}")
    print(f"{files} 个文件，{errors} 个错误")
    sys.exit(1 if errors else 0)


if __name__ == "__main__":
    main()
