## Current phase: core gameplay development

Current version: `0.0.0`.

- Develop with agile, playable slices: choose the smallest end-to-end change that answers the current gameplay question.
- Work in short iterations and validate each slice before expanding scope.
- Keep architecture, abstractions, content, polish, and extensibility proportional to the current slice; prefer reversible decisions while the design is still changing rapidly.
- Treat the current game, domain model, and roadmap as provisional. Features may be revised, replaced, added, or removed when new evidence changes the direction.
- Stop each iteration when there is enough evidence to choose the next small step.
- Keep future systems and content ideas in the issue tracker or roadmap until they are selected for the current slice; a proposed direction is not an implementation commitment.

## Decision ladder

For coding tasks, stop at the first option that fully solves the task:

1. Decide whether the change needs to exist.
2. Reuse an existing implementation from the codebase.
3. Use the standard library.
4. Use a native platform feature.
5. Use an already-installed dependency.
6. Use one clear line when it remains readable and correct.
7. Otherwise, write the minimum code that works.

## Complexity constraints

- Prefer boring code over clever code.
- Avoid unrequested abstractions, single-use factories, single-implementation interfaces, speculative configuration, unnecessary dependencies, and scaffolding for future needs.
- Use the fewest files and smallest correct diff.

## Tests and documentation

- Add tests for realistic observable regressions, non-trivial invariants or boundaries, and concrete bugs. Code churn or increased coverage is not sufficient justification by itself.
- Prefer existing coverage at the behavior boundary. Avoid tests that mirror literals, mappings, obvious control flow, incidental implementation details, or removed features unless absence is itself a contract.
- For concurrency, prefer deterministic coordination or controlled scheduling over sleeps when practical.
- Comments should explain non-obvious rationale, invariants, safety constraints, or external quirks rather than restating code.
- Public API documentation should describe observable contracts, not incidental implementation details.

## Agent skills

### Issue tracker

Issues and specs are tracked in GitHub Issues for `odradekk/train`. See `docs/agents/issue-tracker.md`.

### Triage labels

Use the five default canonical triage labels. See `docs/agents/triage-labels.md`.

### Domain docs

Use a single-context layout with `CONTEXT.md` and `docs/adr/` at the repository root. See `docs/agents/domain.md`.
