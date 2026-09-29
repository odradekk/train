# 项目级 Godot MCP

本仓库统一使用自行维护的 [odradekk/godot-mcp](https://github.com/odradekk/godot-mcp)，后续更新也以此 fork 为来源。通过 stdio 启动，当前固定提交为 `cf7d6e6af4fbc7a6b2d2c7adccd1ca8263fcfb44`（含属性赋值、UID 与运行日志修复），不再使用 `@coding-solo/godot-mcp` 的 npm 发布包。三个客户端均将服务器命名为 `godot`：

| 客户端 | 仓库内配置 | 官方说明 |
| --- | --- | --- |
| Codex | `.codex/config.toml` | [项目配置与 MCP](https://developers.openai.com/codex/mcp) |
| Claude Code | `.mcp.json` | [Project scope](https://code.claude.com/docs/en/mcp#project-scope) |
| Cursor | `.cursor/mcp.json` | [Configuration locations](https://cursor.com/docs/mcp#configuration-locations) |

这些文件只注册本项目的 MCP，不写入用户级配置，也不执行 `npm install -g`。启动命令为 `npx --yes --package=git+https://github.com/odradekk/godot-mcp.git#cf7d6e6af4fbc7a6b2d2c7adccd1ca8263fcfb44 godot-mcp`。`npx` 首次启动会获取该 Git 提交、安装依赖并执行仓库的 `prepare` 构建，随后使用 npm 缓存；共享缓存不代表全局注册 MCP。更新时同步修改三个配置中的提交 SHA，并验证新版本；不会自动跟随分支更新。

## 使用

当前配置针对仓库的 Windows 开发环境，需要 PATH 中有 Node.js 18+、npm 和 Git。`cmd.exe /d /c` 用来运行 Windows 的 `npx` 启动器。`GODOT_PATH` 显式指向本机 Godot 4.7.2 .NET 的 console 可执行文件，与 [本地验证约定](tickets-sop.md#local-validation-and-windows-builds) 一致。更换 Godot 安装位置时，同步修改三个配置的 `GODOT_PATH`。

1. 在该仓库或包含这些配置的工作树中打开客户端，重新启动会话以加载配置。
2. Codex 需要信任项目才加载 `.codex/config.toml`；Claude Code 交互模式首次使用会要求批准项目 MCP；Cursor 如提示批准或启用 `godot`，按客户端提示操作。这些配置不自动放开工具权限。
3. 在项目根目录检查配置：

   ```powershell
   codex mcp get godot --json
   claude mcp get godot
   agent mcp list-tools godot
   ```

   最后一条使用 Cursor CLI；Cursor 编辑器也可在 MCP 设置中查看状态。Codex/Claude Code 可通过会话内 `/mcp` 查看连接情况。

4. 调用 `get_godot_version` 验证引擎连接；需要 `projectPath` 的工具必须传入**当前工作树**的绝对路径。配置不固定 `D:\Code\train`，也不会把工具的文件访问限制在本项目内；“项目级”指配置的生效范围。

新机器首次启动需要访问 GitHub 和 npm，首次下载及构建可能比缓存启动耗时更长。更换提交后需重启客户端会话，已有 MCP 进程不会自动换版本。该 MCP 能运行项目和操作场景，但不会替代 C# 构建及仓库验证脚本；运行游戏前仍按本地验证约定执行 `dotnet build` 和 Godot 导入。

当前固定提交已验证向量属性赋值、已导入项目的 UID 更新、纹理 UID 查询及进程退出后的错误日志保留。仍有一项边界：新建且尚未导入的 `.gd` 脚本缺少 `.uid` 时，`update_project_uids` 可能明确返回 UID 生成失败；本机对照测试中，先执行 Godot `--headless --path <当前项目> --editor --import` 后即可通过。不要把这个失败当作更新成功，也不要依赖该工具替代首次导入。
