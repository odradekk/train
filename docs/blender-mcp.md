# 项目级 Blender MCP

本项目使用 Blender 官方的 [Blender Lab MCP](https://www.blender.org/lab/mcp-server)（GPL-3.0+），版本 1.0.3，用于美术工单制作和检查自定义模型。它分两部分：Blender 扩展 `mcp` 在 Blender 进程内监听本机 TCP 端口；MCP 服务 `blender-mcp` 由客户端通过 uvx 以 stdio 方式启动，按环境变量 `BLENDER_MCP_PORT` 连接 Blender。

Claude Code 的 `.mcp.json` 和 Codex 的 `.codex/config.toml` 都把服务器命名为 `blender`，服务固定到 v1.0.3 对应的提交 `2cea8d566dde07fbac28a61d698909d69724e853`，默认端口 9876。Codex 另设 120 秒启动超时和 300 秒工具超时。

## 安装（每台机器一次）

- 扩展要求 Blender 5.1 及以上。本机使用 Steam 安装的 Blender 5.2.2 LTS：`D:\SteamLibrary\steamapps\common\Blender\blender.exe`。
- 下载[发布页](https://projects.blender.org/lab/blender_mcp/releases)的 `mcp-1.0.3.zip`，执行 `blender --command extension install-file -r user_default -e <zip 路径>`。扩展装在 `%APPDATA%\Blender Foundation\Blender\5.2\extensions\user_default\mcp\`。
- Steam 升级到新的次版本（例如 5.3）会换用户目录，需要重装扩展。Steam 中 Blender 的更新方式宜设为仅在启动时更新，避免 agent 运行中被替换。
- 首次运行 uvx 会下载并构建服务（实测约 90 秒），之后使用缓存。

## 启动

```powershell
& 'D:\SteamLibrary\steamapps\common\Blender\blender.exe' --online-mode --background [文件.blend] --command blender_mcp --port <端口>
```

- 无界面运行，只监听 127.0.0.1。缺少 `--online-mode` 时扩展拒绝启动。
- 启动前确认端口空闲：扩展的套接字允许端口复用，Windows 上第二个实例会静默占用同一端口。
- 并行任务时每个工作树用自己的端口：协调者启动实例，在该工作树里把 `.mcp.json` 和 `.codex/config.toml` 中的 `BLENDER_MCP_PORT` 改成同一端口，并执行 `git update-index --skip-worktree .mcp.json .codex/config.toml`。
- 2026-10-12 实测：两个实例同时运行互不干扰；Paseo 启动的 Claude 和 Codex agent 都能调用。

## 使用与限制

- 建模和导出都用 `execute_blender_code` 执行 bpy 代码：导出用 `bpy.ops.export_scene.gltf(filepath=..., export_format='GLB')`，保存源文件用 `bpy.ops.wm.save_as_mainfile(filepath=...)`。
- 无界面模式没有视口截图，`get_screenshot_of_*` 不可用。需要出图时，在 `execute_blender_code` 里设置输出路径后调用 `bpy.ops.render.render(write_still=True)`；`render_viewport_to_path` 和 `render_thumbnail_to_path` 会忽略指定目录，写到临时目录。
- 不要使用 `*_for_cli` 工具，它们在 Windows 上会挂起。
- Codex 生成首轮工具列表时只为可选 MCP 服务等待约 1 秒；服务冷启动较慢时，`blender` 工具从第二轮起才出现。
- 服务会执行任意 Python 代码，没有认证和沙箱，只监听本机。不要开放远程访问。
