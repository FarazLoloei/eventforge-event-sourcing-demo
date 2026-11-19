---
name: quality-review
description: Review EventForge changes, including AI-generated code, for xUnit test quality, architecture and security guardrails, and verified acceptance criteria before the owner accepts them.
---

# EventForge Quality Review

`AGENTS.md` ("Incremental tests and review", "Security and intellectual property") is authoritative. This skill is a short review procedure. It creates no gates and no tooling.

Current status: see the README for what is implemented and verified. No tests, CI or architecture checks exist unless implemented and verified. Never claim otherwise, and don't add scaffolding to satisfy a documentation-only task.

## Review order

1. **Scope**: do the changed files match the approved Jira task and nothing else?
2. **Acceptance criteria**: map each criterion to concrete evidence (command output, test name, file). Mark each met, not met or not run.
3. **Invariants**: assignment before generation, tests tied to the current generation, completion needs that generation to pass, invalid commands append nothing.
4. **Dependencies**: no outward references, new packages reviewed, no domain access to HTTP/SQL/simulator types.
5. **Compatibility and replay**: event schema versions, upcaster coverage, and no side effects during replay.
6. **Security**: untrusted input validated at adapters, no secrets or personal data in events/logs/fixtures, synthetic data only, no executed generated code, no real AI integration.

## Test expectations (xUnit)

| Kind | Covers | Notes |
| --- | --- | --- |
| Unit | Domain rules, `Decide`/`Apply`, upcaster fixtures | Fast, no I/O, deterministic |
| Integration | PostgreSQL append/load, racing expected versions, multi-event atomicity, projection-failure rollback, committed reads | Real database, not mocks |
| Architecture | Allowed references, domain isolation | Include a forbidden-dependency fixture that must fail |
| Replay/recovery | Healthy diff, corrupted-projection recovery, invalid history, cutover routing | Throwing adapters prove no side effects |

Prefer a few tests with independent expectations over many tests that mirror the implementation.

## AI-generated code

Treat it as an untrusted contribution.
- Check that tests could fail: look for assertions that restate the code, deleted or skipped tests, and loosened thresholds.
- Expected values must come from the spec or hand calculation, not from running the code under test.
- Look for invented APIs, packages or issue keys, and for unrequested abstractions or files.
- A green build shows the code compiles and the tests ran. It doesn't show the design is correct.

## Reporting

- List changed files, the commands actually run with their results, skipped checks with reasons, and known limits.
- Keep the distinction explicit between documented policy and working automation.
- Git and Jira actions belong to the owner. Offer commit and PR text in the response only.
