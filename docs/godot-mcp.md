# 项目级 Godot MCP

本仓库使用 [Godot MCP Native](https://github.com/yurineko73/Godot-MCP-Native)（MIT 许可）。它是 Godot 编辑器插件，在编辑器进程内提供 MCP HTTP 服务，不依赖 Node.js。插件代码随仓库提交在 `addons/godot_mcp/`，取自上游提交 `eef4807190e7765e69d59c9e707d0ba170f6770d`（插件版本 1.0.8 之后的主干，含编辑器日志工具修复），并在 `project.godot` 的 `[editor_plugins]` 中启用。

不要在本仓库修改 `addons/godot_mcp/`。更新时用上游指定提交的 `addons/godot_mcp/` 整体替换，同步本文件中的提交 SHA，并按下文重新验证。

插件启用后，编辑器启动时会在 `project.godot` 注册 `MCPRuntimeProbe` autoload（`addons/godot_mcp/runtime/mcp_runtime_probe.gd`），供运行时调试工具与编辑器通信。没有编辑器调试会话时（包括导出版本）它不做任何事。这项注册已随仓库提交；删掉后编辑器下次启动会重新写入。

三个客户端都把服务器命名为 `godot`，连接 `http://127.0.0.1:9080/mcp`：

| 客户端 | 仓库内配置 | 官方说明 |
| --- | --- | --- |
| Codex | `.codex/config.toml` | [项目配置与 MCP](https://developers.openai.com/codex/mcp) |
| Claude Code | `.mcp.json` | [Project scope](https://code.claude.com/docs/en/mcp#project-scope) |
| Cursor | `.cursor/mcp.json` | [Configuration locations](https://cursor.com/docs/mcp#configuration-locations) |

这些文件只注册本项目的 MCP，不写入用户级配置。

## 启动服务

MCP 服务运行在 Godot 编辑器里，客户端只负责连接。先启动编辑器，再启动或重连客户端会话。在当前工作树执行：

```powershell
& $env:GODOT --headless --editor --path . -- --mcp-server --mcp-port=9080
```

- 必须带 `--mcp-server`。插件默认不自动启动服务，所以普通的 `--editor --import` 不会占用端口。需要图形界面时，可在编辑器的 MCP 面板中点击启动，端口保持 9080。
- 本机实测服务约 6 秒就绪。客户端在会话开始时连接；如果当时服务未就绪，Claude Code 用 `/mcp` 重连，其他客户端重启会话。
- 服务默认拒绝非本机请求，未启用认证。不要开启远程访问。
- 一个端口同时只能由一个编辑器监听。仓库配置默认连接 9080，供主检出使用。并行任务时，每个工作树的编辑器用自己的端口启动；协调者在该工作树里把 `.mcp.json` 和 `.codex/config.toml` 中 `godot` 的端口改成同一个值，并执行 `git update-index --skip-worktree .mcp.json .codex/config.toml`，避免误提交。2026-10-12 实测两个工作树的编辑器可以同时完整使用，包括 `run_project` 和运行时工具；Paseo 启动的 Claude 和 Codex agent 都能连接各自的端口。
- 编辑器运行游戏时还要监听调试端口：从 6007 开始，被占用时自动顺延，最多尝试 5 个，所以最多 5 个编辑器能同时运行游戏。
- 各工作树的游戏共用同一个 `user://`（`%APPDATA%\Godotpp_userdata\Train`），包括存档、日志和 `get_runtime_screenshot` 的默认文件。截图时给 `save_path` 指定带工单号的文件名。
- 客户端连接的是端口，不是工作树。每次会话先调用 `get_project_info`，确认返回的 `project_path` 是当前工作树；不一致说明端口被其他工作树的编辑器占用，不要继续操作。
- 编辑器 MCP 面板的设置保存在 `user://mcp_settings.cfg`，同名项目的各工作树共享这份文件。命令行参数优先于它，但 `--mcp-port` 会把端口写回这份文件，所以每次启动都显式传 `--mcp-port`。不要在其中开启自动启动。

## 客户端检查

1. Codex 需要信任项目才加载 `.codex/config.toml`；Claude Code 交互模式首次使用会要求批准项目 MCP；Cursor 如提示批准或启用 `godot`，按客户端提示操作。这些配置不自动放开工具权限。
2. 在项目根目录检查配置：

   ```powershell
   codex mcp get godot --json
   claude mcp get godot
   ```

   Cursor 在 MCP 设置中查看状态。Codex 和 Claude Code 可通过会话内 `/mcp` 查看连接情况。

3. 调用 `get_project_info`，核对 `project_path` 和 `godot_version`（当前为 `4.7.stable`）。

## 工具与限制

- 插件默认只开启 30 个核心工具，包括项目信息、场景/节点/脚本的查询与编辑、`run_project`、`stop_project` 和 `get_editor_logs`。其余 125 个补充工具（运行时探针、断点、UID、导出等）默认关闭。本项目另外开启[运行时调试与界面操作工具](#运行时调试与界面操作工具)中的 12 个，其他补充工具保持关闭。
- Vibe Coding 模式默认开启：会打开或控制游戏窗口的调用（`run_project`、`stop_project`）必须传 `allow_window=true`，会改变编辑器焦点或选择的调用需要传 `allow_ui_focus=true`，否则工具返回错误。
- `run_project` 通过编辑器播放：先保存编辑器中已打开的场景，再在编辑器内构建 .NET 项目，所以启动编辑器的进程 PATH 必须能找到 `dotnet`（见[本地验证约定](local-environment.md#windows-development-and-validation)）。结束后检查 diff，还原非任务改动。
- 读取游戏输出用 `get_editor_logs` 并设 `source` 为 `editor_panel`（编辑器输出面板，包含 `run_project` 启动的游戏输出），核对时间戳属于本次运行。默认的 `mcp` 只有插件自身日志；`runtime` 读 `user://logs/godot.log`，本机实测取不到本项目的运行日志。
- 编辑器启动时可能为缺少 `.uid` 的脚本生成 `.uid` 文件，属于任务范围外的改动，提交前检查。
- MCP 不负责 C# 构建，也不替代仓库的验证脚本；运行游戏前仍按[本地验证约定](local-environment.md)执行 `dotnet build` 和 Godot 导入。

## 运行时调试与界面操作工具

这些工具通过 `MCPRuntimeProbe` 与 `run_project` 启动的游戏通信，只对编辑器调试会话中的游戏有效，对命令行另行启动的 Godot 进程无效。

| 用途 | 工具 |
| --- | --- |
| 查看运行中的游戏 | `get_runtime_info`、`get_runtime_scene_tree`、`inspect_runtime_node`、`evaluate_runtime_expression`、`get_runtime_screenshot`、`get_runtime_performance_snapshot` |
| 等待与断言 | `await_scene_ready`、`await_runtime_condition`、`assert_runtime_condition` |
| 注入输入 | `list_runtime_input_actions`、`simulate_runtime_input_action`、`simulate_runtime_input_event` |

- `simulate_runtime_input_*` 在游戏进程内调用 `Input.parse_input_event` 注入事件，经过 Godot 的界面输入处理，可以点击按钮和输入按键。它不经过系统鼠标和窗口，不能代替 Issue 要求的人工检查，也不能证明窗口层面的行为。
- 验证要通过界面输入或游戏自身的命令推进。`evaluate_runtime_expression` 只用来读取状态；修改运行时节点属性、调用节点方法、创建或删除节点的工具因此不开启。
- 截图和日志只证明画面或输出，不代替断言。
- 断点、单步等调试器工具只作用于 GDScript，本项目代码是 C#，不开启。

开关状态保存在编辑器用户数据目录的 `mcp_tool_state.cfg`（Windows 为 `%APPDATA%\Godot\app_userdata\Train\`），同名项目的各工作树共享这份文件。文件带校验和，不要手改。新机器或状态丢失时，启动 MCP 编辑器后用 `execute_editor_script` 执行一次：

```gdscript
var server = Engine.get_meta("GodotMCPPlugin")._native_server
for tool_name in ["get_runtime_info", "get_runtime_scene_tree", "inspect_runtime_node",
		"evaluate_runtime_expression", "get_runtime_screenshot", "get_runtime_performance_snapshot",
		"await_scene_ready", "await_runtime_condition", "assert_runtime_condition",
		"list_runtime_input_actions", "simulate_runtime_input_action", "simulate_runtime_input_event"]:
	server.set_tool_enabled(tool_name, true)
server.save_tool_states()
server.notify_tool_list_changed()
```

之后编辑器启动时会读取这份状态。客户端如果没有显示新开启的工具，按[启动服务](#启动服务)中的方法重连。
