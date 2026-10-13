The working language is Chinese.

## Current phase: core gameplay development

Current version: `0.0.56`.

- Develop with agile, playable slices: choose the smallest end-to-end change that answers the current gameplay question.
- Work in short iterations and validate each slice before expanding scope.
- Keep architecture, abstractions, content, polish, and extensibility proportional to the current slice; prefer reversible decisions while the design is still changing rapidly.
- Treat the current game, domain model, and roadmap as provisional. Features may be revised, replaced, added, or removed when new evidence changes the direction.
- Stop each iteration when there is enough evidence to choose the next small step.
- Keep future systems and content ideas in the issue tracker or roadmap until they are selected for the current slice; a proposed direction is not an implementation commitment.

## Code taste and design principles

Apply these when implementing, reviewing, and writing docs. Every piece of added complexity must name the current problem it solves.

- **Less is more.** Finish the task with the fewest lines, files, types, and concepts. First decide whether the change is needed at all; then reuse existing repository code, the .NET standard library, or built-in Godot features before writing new code. Delete or inline helpers, wrappers, and settings that have no current use.
- **KISS.** Write plain code a reader understands in one pass. Ordinary methods, data types, and explicit branches come before reflection, code generation, generic frameworks, or clever tricks.
- **Composition over inheritance.** Build behaviour by holding or receiving collaborators, and express module boundaries as interfaces. Abstract records model closed variant sets such as results, commands, and reference kinds; share code through composition rather than base classes or inheritance chains. Godot node scripts inherit node types as the engine requires; plain .NET types stay independent of `GodotObject`.
- **Information hiding** keeps modules decoupled. Each module exposes the smallest contract its callers need and keeps internal state, data structures, and algorithms `internal` or `private`. Callers depend on contracts rather than implementations, and pass other modules' opaque data, such as save payloads, through unread. A change to one module's internals should leave other modules untouched; when it does not, the contract exposes too much, so fix the contract.
- **YAGNI: spend no complexity on features nobody needs yet.** Implement only the behaviour the current issue and slice require. Add parameters, switches, extension points, and compatibility layers when a real need arrives. Optimise performance on profiling evidence.
- **Avoid over-engineering.** Keep indirection to what the current design needs, without generic frameworks or catch-all base classes. Use interfaces for module contracts and for boundaries whose implementation must be swappable, such as host ports and test doubles; a class with one implementation and no need to swap it stays concrete. The project's named anti-patterns are listed under [设计指导思想](docs/design/README.md#设计指导思想).
- **Code over comments.** Make code readable through naming and structure. Write a comment only for a reason the code cannot show, such as an ordering required for determinism, an engine or platform quirk, or a check that looks redundant.

## Source layout

- Production code has four projects at the repository root. The legacy behavior has been separated by responsibility; the newer contract factories and full Demo behavior are still pending:
  - `Core/Train.Core.csproj`: core simulation, with no Godot or other implementation reference.
  - `Content/Train.Content.csproj`: content loading, validation, publication and path checks, with no other implementation reference.
  - `Scenes/Train.Scenes.csproj`: voxel engine layer, with world-session lifecycle, immutable frames, cube meshes, picking and read-only design previews. Route, node and battle generation remain assigned to #315, #316 and #353; UV rules to #248; other shapes/models to #388; editing to #333; template previews to #389.
  - Root `Train.csproj`: Godot host, referencing the current implementations. Startup, logging, console dispatch, file saves and content adapters live in `App/`; rendering code goes in `Rendering/`, UI in `Ui/`.
- `Contracts/Core/`, `Contracts/Content/`, and `Contracts/Scenes/` are three separate contract projects. `prototypes/*-contract/` holds four sets of compilable call samples, not production code. The confirmed declaration baseline is [public-api.txt](docs/contracts/public-api.txt); scope history is in the [#302 handoff](docs/contracts/baseline-candidate.md#候选范围).
- `Tests/Core/Train.Core.Tests.csproj` tests Core only; `Tests/Content/Train.Content.Tests.csproj` tests Content only; `Tests/Scenes/Train.Scenes.Tests.csproj` tests Scenes with fixed Core/Content read views and controlled generation; `Tests/Godot/Train.Godot.Tests.csproj` tests host code through the root project. `Tests/Engine/Train.Engine.Tests.csproj` runs current App integration tests in real Godot with `scripts/test.ps1 -Layer engine`; the root project compiles `Tests/Engine/Bootstrap/` only in Debug, excluding it from Release, ExportDebug and ExportRelease.
- `Tests/ContractSupport/` holds class libraries, split by interface owner, with shared scenarios, assertions, and finite-scenario test doubles; their self-tests are in `Tests/ContractSupport/SelfTests/Train.ContractSupport.Tests.csproj`. Pure host tests do not establish real Godot behavior or module isolation. Entry and remaining gaps: [test organization](docs/contracts/testing.md#模块测试的组织与入口).
- `Tests/ContractChecks/Train.ContractChecks.csproj` is the CLI for structure, public API comparison and Core/Content/Scenes isolation; `Tests/ContractChecks.Tests/Train.ContractChecks.Tests.csproj` tests this tool. Both are in `Train.sln`; the four call samples remain outside it and are built by the tool. Commands and selection: [test entry](docs/run-tests.md).
- Before designing or changing module interfaces, project references, cross-layer types, or host composition, read the [four-module contract specification](docs/contracts/README.md) and its shared conventions. It specifies the target structure, which the code above has not migrated to yet; it is neither a finished implementation nor independent test evidence.
- Namespaces follow the project name plus the directory path, e.g. `Train.Content.Behaviors`. Divide deeper directories by the responsibilities of the current task.
- Treat `archive/mvp/` as read-only validation evidence. Reuse an archived implementation only by deliberately moving or rewriting the necessary code into the root project; do not continue product development inside the archive.
- Keep generated Godot state, build outputs, and restored packages out of the archive; regenerate them only when reproducing an MVP result.

## Local development environment

- Development runs on Windows; validation scripts use PowerShell 7 (`pwsh`). Run `dotnet --version` in the project directory to confirm the SDK pinned by `global.json`.
- Godot .NET is installed at `D:\Godot_v4.7.2-stable_mono_win64\`. The `GODOT` environment variable points to `Godot_v4.7.2-stable_mono_win64_console.exe` in that directory, with no arguments; run headless checks with `& $env:GODOT --headless`.
- Before configuring a new terminal, running validation, or exporting a Windows build, read [Local validation and Windows builds](docs/local-environment.md).

## Agent skills

### Issue tracker

Issues and specs are tracked in GitHub Issues for `odradekk/train`. See `docs/agents/issue-tracker.md`.

### Triage labels

Use the five default canonical triage labels. See `docs/agents/triage-labels.md`.

### Area and discipline labels

When creating or triaging an issue, apply `area:` and `discipline:` labels per `docs/agents/area-and-discipline-labels.md`.

### Domain docs

Use a single-context layout with `CONTEXT.md` and `docs/adr/` at the repository root. See `docs/agents/domain.md`.

### Issue task workflow

Each Issue is done by an agent pair in Paseo; an individual task's scope and Acceptance criteria live in its Issue. Read the SOP for your role:

- **Coordinator** (scheduling, Paseo workspaces and reviewers, merge, closure, progress, stage acceptance and version): [Dispatch SOP](docs/dispatch-sop.md).
- **Reviewer** (creates and directs the worker, reviews the plan and delivery for correctness, direction and code taste, verifies Acceptance criteria): [Review SOP](docs/review-sop.md).
- **Worker** (implementation, testing, PR delivery and review fixes): [Implementation SOP](docs/implement-sop.md). It also holds the Testing standard and Godot MCP validation.

### Console command documentation

When using or reviewing console commands, start from the manual at `docs/console/README.md`. When adding, changing, or removing a command, update its page in `docs/console/` (one file per command) and the index.
