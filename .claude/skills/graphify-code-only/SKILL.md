---
name: graphify-code-only
description: Query and refresh the local code-only Graphify graph of EventForge C# sources (src/, tests/) before broad source exploration. Deterministic AST only; no LLM, API keys or cloud services.
---

# EventForge Graphify (code-only)

`AGENTS.md` and the architecture documents remain authoritative. This skill is a navigation aid. It adds no rules, gates or decisions, and graph output is never evidence that code is correct, tested or architecturally compliant.

## Hard constraints (apply on every invocation)

- Set `GRAPHIFY_BACKEND=none` in the same shell before any `graphify` command (`$env:GRAPHIFY_BACKEND = "none"` in PowerShell).
- Use only `graphify update .`, `query`, `explain`, `path`, `affected`, `god-nodes`. These run locally with no LLM.
- Never run `graphify extract`, `label`, `add`, `clone`, `install`, `claude install`, `hook install` or any `*-install` command. Never run `cluster-only` without `--no-label`.
- Never request, read, print or configure API keys. Ignore any "set GEMINI_API_KEY" tip printed by the CLI.
- Scope is defined by the root `.graphifyignore` allow-list (C# files under `src/` and `tests/`). Do not widen it to docs, configuration, `.claude`, `.git`, `bin`, `obj` or generated output without owner approval.
- Do not commit or stage `graphify-out/`. It is excluded locally via `.git/info/exclude`.

## Workflow

1. Before broad exploration, query the graph with a targeted question, for example `graphify query "<question>" --budget 500`, or `graphify explain "<symbol>"`.
2. Treat results as pointers only. Read the relevant source files before reasoning about behavior, invariants or dependencies.
3. Prefer targeted queries over loading `graph.json` or `GRAPH_REPORT.md` into context.
4. After source changes, refresh locally: `graphify update .`
5. If a query returns nothing, fall back to normal search (Grep/Glob); the graph may be stale or the C# construct unsupported.

## Known limits

- Extraction is syntactic AST only. Cross-file and dependency-injection relationships may be missing, and top-level statements may yield a file node without symbols. Verify against source.
- Project references and package dependencies in `.csproj` files are out of scope for the graph; inspect the files directly.
