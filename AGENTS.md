The working language is Chinese.

## Current phase: core gameplay development

Current version: `0.6.0`.

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

- Production code has four projects at the repository root. The Godot host uses the real contract factories through `App/Composition/`; full Demo behavior is still pending:
  - `Core/Train.Core.csproj`: core simulation, with no Godot or other implementation reference.
  - `Content/Train.Content.csproj`: content loading, validation, publication and path checks, with no other implementation reference.
  - `Scenes/Train.Scenes.csproj`: voxel engine layer, with world-session lifecycle, immutable frames, cube meshes, picking, read-only design previews and railway generation. Node generation was delivered by #316 and combat presentation by #353; other shapes/models to #388; editing to #333; template previews to #389.
  - Root `Train.csproj`: Godot host, referencing the current implementations. Startup, logging, console dispatch, file saves and content adapters live in `App/`; rendering code goes in `Rendering/`, UI in `Ui/`.
- `Tools/Cli/Train.Cli.csproj` is the pure .NET host tool. It consumes Core/Content contracts and creates their implementations only in `Composition.cs`; it does not reference Godot or Scenes. It links the host's `App/Assets/DiskContentFiles.cs`, `App/Assets/GlbInput.cs`, `App/Saves/SaveContainer.cs`, `App/Saves/FileSaveStore.cs`, `App/RuntimeEnvironment.cs` and `App/Measurement/WorldCounts.cs` as exact source exceptions. `Tests/Cli/` verifies its ports and real subprocess behavior.
- `Contracts/Core/`, `Contracts/Content/`, and `Contracts/Scenes/` are three separate contract projects. `prototypes/*-contract/` holds four sets of compilable call samples, not production code. The confirmed declaration baseline is [public-api.txt](docs/contracts/public-api.txt); scope history is in the [#302 handoff](docs/contracts/baseline-candidate.md#候选范围).
- `Tests/Core/Train.Core.Tests.csproj` tests Core only; `Tests/Content/Train.Content.Tests.csproj` tests Content only; `Tests/Scenes/Train.Scenes.Tests.csproj` tests Scenes with fixed Core/Content read views and controlled generation; `Tests/Godot/Train.Godot.Tests.csproj` tests host code through the root project. `Tests/Engine/Train.Engine.Tests.csproj` runs current App integration tests in real Godot with `scripts/test.ps1 -Layer engine`; the root project compiles `Tests/Engine/Bootstrap/` only in Debug, excluding it from Release, ExportDebug and ExportRelease.
- `Tests/ContractSupport/` holds class libraries, split by interface owner, with shared scenarios, assertions, and finite-scenario test doubles; their self-tests are in `Tests/ContractSupport/SelfTests/Train.ContractSupport.Tests.csproj`. Pure host tests do not establish real Godot behavior or module isolation. Entry and remaining gaps: [test organization](docs/contracts/testing.md#模块测试的组织与入口).
- `Tests/ContractChecks/Train.ContractChecks.csproj` is the CLI for structure, public API comparison and Core/Content/Scenes/Godot isolation; `Tests/ContractChecks.Tests/Train.ContractChecks.Tests.csproj` tests this tool. Both are in `Train.sln`; the four call samples remain outside it and are built by the tool. Commands and selection: [test entry](docs/run-tests.md).
- Before designing or changing module interfaces, project references, cross-layer types, or host composition, read the [four-module contract specification](docs/contracts/README.md) and its shared conventions. It specifies the module boundaries and remaining target behavior; it is neither a complete Demo implementation nor independent test evidence.
- Namespaces follow the project name plus the directory path, e.g. `Train.Content.Behaviors`. Divide deeper directories by the responsibilities of the current task.
- Treat `archive/mvp/` as read-only validation evidence. Reuse an archived implementation only by deliberately moving or rewriting the necessary code into the root project; do not continue product development inside the archive.
- Keep generated Godot state, build outputs, and restored packages out of the archive; regenerate them only when reproducing an MVP result.

## Local development environment

- Development runs on Windows; validation scripts use PowerShell 7 (`pwsh`). Run `dotnet --version` in the project directory to confirm the SDK pinned by `global.json`.
- Godot .NET is installed at `D:\Godot_v4.7.2-stable_mono_win64\`. The `GODOT` environment variable points to `Godot_v4.7.2-stable_mono_win64_console.exe` in that directory, with no arguments; run headless checks with `& $env:GODOT --headless`.
- Before configuring a new terminal, running validation, or exporting a Windows build, read [Local validation and Windows builds](docs/local-environment.md).

## Agent workflow

Maintained with the `setup-agent-workflow` skill. Edit values freely; keep the field names.

- Base branch: `master`
- Decision notes: `.agents/notes/`, written in Chinese with English section headings and ASCII file names. Every decision record, ADRs included, is a decision note, written during planning with the `decision-notes` skill; work carries out accepted notes and never edits a note. Notes in `accepted/`, `rejected/`, and `archived/` are frozen and read-only: only the `decision-notes` skill's `note_lifecycle.py` changes them, on the user's word.
- Tracker: GitHub issues on `origin` (`odradekk/train`), through `gh`. Tickets are referenced as `#N`. A pull request references its ticket with `Refs #N` and does not close it; the coordinator closes the ticket after merge, or leaves it open with `awaiting-user` while the user still has to check it. The tracker records blocking relations and labels natively. Tickets for decision notes follow the `note-to-tickets` skill. Commits on a pull request branch carry no ticket reference.
- Labels: every issue and pull request gets exactly one `kind/*` label and every `area/*` label that applies. Use only the labels listed here; when none fits, refresh this section with the `setup-agent-workflow` skill.
  - `kind/feature`: a new player-, modder-, or developer-facing capability. `kind/bug-fix`: corrects a defect. `kind/simplification`: removes code, behavior, or surface without adding capability. `kind/architecture`: the structure of the shipped code. `kind/performance`: speed or memory of existing behavior, measured before and after. `kind/process`: tooling and workflow around the code. `kind/testing`: test infrastructure and strategy. `kind/docs`: documentation-only work.
  - Tests belong to the area of the code they test. `area/simulation`: `Core/**` outside the domain folders below. `area/world`: `Core/World/**`. `area/railway`: `Core/Railway/**`, `Scenes/Railway*`. `area/trade`: `Core/Trading/**`. `area/production`: `Core/Production/**`. `area/settlements`: `Core/Settlements/**`, `Scenes/Settlement*`. `area/construction`: `Core/Construction/**`. `area/combat`: `Core/Combat/**`, `Scenes/Combat*`, `Rendering/CombatEffects*`, `Ui/CombatPanel*`. `area/content`: `Content/Loading/**`, `Content/Designs/**`, `builtin/**`, `examples/**`, `docs/content/**`, `scripts/content/**`. `area/saves`: `Content/Persistence/**`, `App/Saves/**`. `area/editor`: `Content/Authoring/**`, `Scenes/Editing/**`, `App/Authoring/**`, `App/Editing/**`, `Ui/DesignEditor*`. `area/scenes`: `Scenes/**`. `area/rendering`: `Rendering/**`. `area/ui`: `Ui/**`. `area/host`: `App/**` outside the other areas, `Train.csproj`, `project.godot`. `area/contracts`: `Contracts/**`, `docs/contracts/**`, `prototypes/**`, `App/Composition/**`. `area/tooling`: `scripts/**`, `Tools/**`, `Tests/ContractChecks*/**`, `App/Measurement/**`, `.github/**`, `.agents/**`, and the test, CI, and environment docs. `area/art`: `art-source/**`, `docs/art/**`, `App/ArtCapture/**`. `area/design`: `docs/design/**`, `docs/research/**`, `CONTEXT.md`.
  - Outside the scheme: `awaiting-user` marks finished work that waits for the user's own check; `ci:correctness` on a pull request requests one full Windows correctness run ([CI](docs/ci.md)).
- Prose: documents, decision notes, tickets, pull request descriptions, and commit bodies are revised with the `plain-language` skill. House style: Chinese prose with established terms, following the `CONTEXT.md` glossary; design documents cite 转录 Qn, never §n.
- File size: files stay under 5,000 tokens (bytes / 4) so an agent can read them whole. `.agents/file-size.json` lists excluded paths, exceptions with their reasons, and the baseline of older files over the limit, which may shrink but not grow. The pre-commit hook and CI run `.agents/scripts/check_file_size.py`.
- Hooks: pre-commit runs `.agents/scripts/check_file_size.py` on staged files; commit-msg removes AI attribution lines and pre-push rejects commits that still credit an AI tool, both through `.agents/scripts/strip_attribution.py`; post-checkout, post-merge, and post-rewrite make frozen decision notes read-only again through `.agents/scripts/note_lifecycle.py`. They need Python 3.10 or later on PATH. Connect them once per clone with `git config core.hooksPath .agents/hooks`; Paseo worktrees share the main clone's setting.
- CI: GitHub Actions `windows-checks.yml` runs the file size check on every tracked file and `scripts/test.ps1 -Layer fast,integration` on the projects a pull request to `master` affects, then a non-blocking engine layer, on Windows. The `ci:correctness` label requests one `-All -Layer all` run. The self-hosted Linux runner runs `-All -Layer all,stage` nightly and on every push to `master`.

| Change | Narrowest check |
|---|---|
| Behavior in `Core/**` | `dotnet test --project Tests/Core/Train.Core.Tests.csproj --filter "FullyQualifiedName~<Class>.<Method>"` |
| Behavior in `Content/**` | `dotnet test --project Tests/Content/Train.Content.Tests.csproj --filter "FullyQualifiedName~<Class>.<Method>"` |
| Built-in content data in `builtin/**` | `dotnet test --project Tests/Content/Train.Content.Tests.csproj --filter "FullyQualifiedName~BuiltInContentTests"` |
| Content examples and schemas in `docs/content/**` | `pwsh -NoProfile -File scripts/checks/validate-content-examples.ps1` |
| Behavior in `Scenes/**` | `dotnet test --project Tests/Scenes/Train.Scenes.Tests.csproj --filter "FullyQualifiedName~<Class>.<Method>"` |
| Host code in `App/**`, `Rendering/**`, `Ui/**` that runs without Godot | `dotnet test --project Tests/Godot/Train.Godot.Tests.csproj --filter "FullyQualifiedName~<Class>.<Method>"` |
| Measurement code in `App/Measurement/**` | `dotnet test --project Tests/Godot/Train.Godot.Tests.csproj --filter "FullyQualifiedName~Train.Godot.Tests.Measurement"`; the scenario rules are in `App/Measurement/AGENTS.md` and the `train-performance` skill |
| Host behavior that needs a running Godot | `pwsh -NoProfile -File scripts/test.ps1 -Layer engine`; one engine test runs through the manual route in [the test entry](docs/run-tests.md) |
| `Tools/Cli/**` | `dotnet test --project Tests/Cli/Train.Cli.Tests.csproj --filter "FullyQualifiedName~<Class>.<Method>"` |
| `Tests/ContractSupport/**` | `dotnet test --project Tests/ContractSupport/SelfTests/Train.ContractSupport.Tests.csproj --filter "FullyQualifiedName~<Class>.<Method>"` |
| `Tests/ContractChecks/**` | `dotnet test --project Tests/ContractChecks.Tests/Train.ContractChecks.Tests.csproj --filter "FullyQualifiedName~<Class>.<Method>"` |
| Public declarations in `Contracts/**` | `dotnet build Tests/ContractChecks/Train.ContractChecks.csproj`, then from the repository root `dotnet Tests/ContractChecks/bin/Debug/net10.0/Train.ContractChecks.dll verify . Debug docs/contracts/public-api.txt` |
| Project references and layout: `*.csproj`, `Directory.Build.*`, `Train.sln`, `prototypes/**`, `scripts/checks/project-boundaries.json` | `dotnet build Train.sln`, then `dotnet Tests/ContractChecks/bin/Debug/net10.0/Train.ContractChecks.dll structure . Debug <temp dir>` |
| Cross-module types and host composition in `App/Composition/**` | `dotnet build Train.sln`, then `dotnet Tests/ContractChecks/bin/Debug/net10.0/Train.ContractChecks.dll isolate . Debug <temp dir>` |
| `scripts/**/*.ps1`, `scripts/**/*.py` | No tests cover them; run the changed script once on a real input. |
| Other `**/*.md` | No check; review only. |

## Agent roles and project docs

### Domain glossary

`CONTEXT.md` is the project glossary. Use its terms in code, documents, notes, and tickets, and avoid the words it lists under _Avoid_.

### Issue task workflow

Each Issue is done by an agent pair in Paseo; an individual task's scope and Acceptance criteria live in its Issue. Read the SOP for your role:

- **Coordinator** (scheduling, Paseo workspaces and reviewers, merge, closure, progress, stage acceptance and version): [Dispatch SOP](docs/dispatch-sop.md).
- **Reviewer** (creates and directs the worker, reviews the plan and delivery for correctness, direction and code taste, verifies Acceptance criteria): [Review SOP](docs/review-sop.md).
- **Worker** (implementation, testing, PR delivery and review fixes): [Implementation SOP](docs/implement-sop.md). It also holds the Testing standard and Godot MCP validation.

### Console command documentation

When using or reviewing console commands, start from the manual at `docs/console/README.md`. When adding, changing, or removing a command, update its page in `docs/console/` (one file per command) and the index.
