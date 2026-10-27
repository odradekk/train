"""规模输入命令的执行、日志和内容标识。"""

import hashlib
import json
import subprocess
from pathlib import Path


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def write(path, value):
    # 配方和修改后的内容在 Windows/Linux 上使用相同的 UTF-8、LF 字节。
    path.write_bytes((json.dumps(value, ensure_ascii=False, separators=(",", ":"), allow_nan=False) + "\n").encode("utf-8"))


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def tree_identity(directory):
    files = {path.relative_to(directory).as_posix(): sha(path)
             for path in sorted(directory.rglob("*")) if path.is_file()}
    entries = "".join(name + "\0" + digest + "\n" for name, digest in sorted(files.items()))
    return {"sha256": hashlib.sha256(entries.encode("utf-8")).hexdigest(), "files": files}


def input_identity(directory, variant):
    return {"recipe_sha256": sha(directory / "recipe.json"),
            "content_tree": tree_identity(directory / f"inputs/{variant['multiple']}x/builtin"),
            "save_sha256": sha(directory / variant["save"])}


class ScaleRun:
    def __init__(self, root, output, configuration, operation):
        self.root, self.output, self.configuration = root, output, configuration
        self.probe = root / f"Tools/SceneScale/bin/{configuration}/net10.0/Train.SceneScale.dll"
        self.cli = root / f"Tools/Cli/bin/{configuration}/net10.0/Train.Cli.dll"
        self.manifest = {"operation": operation, "stage": "prepare", "completed": False, "variants": []}
        self.manifest_path = output / ("scale-manifest.json" if operation == "generate" else "scale-verification.json")

    def save(self):
        write(self.manifest_path, self.manifest)

    def command(self, arguments, name, timeout, json_output=False):
        self.manifest["stage"] = name

        def log(value):
            for directory, label in ((self.output, "<temp>"), (self.root, "<workspace>")):
                value = value.replace(str(directory), label).replace(directory.as_posix(), label)
            (self.output / (name + ".log")).write_text(value, encoding="utf-8")

        try:
            result = subprocess.run(list(map(str, arguments)), cwd=self.root, capture_output=True,
                                    text=True, encoding="utf-8", errors="replace", timeout=timeout)
        except subprocess.TimeoutExpired as error:
            # TimeoutExpired 的捕获输出可为 bytes，也可为 str，保留两种平台的失败证据。
            def decoded(value):
                return value.decode("utf-8", errors="replace") if isinstance(value, bytes) else value or ""
            log(decoded(error.stdout) + decoded(error.stderr))
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
                write(self.output / (name + ".json"), value)
        if result.returncode:
            if value is not None:
                self.manifest["failed_observation"] = name + ".json"
            raise RuntimeError(name + "_failed")
        return value if json_output else result.stdout

    def observe(self, repository, mode, save, name, verify_stability=False):
        arguments = ["dotnet", self.probe, repository, mode, save]
        if verify_stability:
            arguments.append("--verify-stable")
        # 五个位置各有 300 秒上限，额外时间留给加载和退出。
        return self.command(arguments, name, 1620 if mode == "local" else 360, json_output=True)

    def prepare(self):
        self.manifest["source_head"] = self.command(["git", "rev-parse", "HEAD"], "source-head", 30).strip()
        self.manifest["source_dirty"] = bool(self.command(["git", "status", "--porcelain"], "source-status", 30).strip())
        if self.manifest["source_dirty"]:
            raise ValueError("source_must_be_clean")
        projects = ("Cli", "SceneScale") if self.manifest["operation"] == "generate" else ("SceneScale",)
        for project in projects:
            self.command(["dotnet", "build", self.root / f"Tools/{project}/Train.{project}.csproj",
                          "-c", self.configuration, "--nologo"], "build-" + project.lower(), 300)
        self.manifest.update({
            "configuration": self.configuration,
            "sources_sha256": {"scripts/measure/" + path.name: sha(path) for path in
                               (Path(__file__), Path(__file__).with_name("scene-scale.py"))},
            "builtin_identity": tree_identity(self.root / "builtin"),
            "assemblies_sha256": {path.name: sha(path) for path in sorted(self.probe.parent.glob("Train.*.dll"))},
            "identity_method": "File bytes SHA256; tree SHA256 of sorted UTF-8 path + NUL + lowercase file SHA256 + LF. "
                "Compare recipe and content tree across platforms; save bytes are recorded for local binding until cross-platform identity is confirmed."})
        self.manifest["sources_sha256"].update({path.relative_to(self.root).as_posix(): sha(path)
            for path in sorted([*self.root.glob("Tools/SceneScale/*.cs"),
                                self.root / "Tools/SceneScale/Train.SceneScale.csproj"])})
        self.save()
