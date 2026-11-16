## Summary

<!-- What does this PR change, and why? Describe only the actual changes. -->

## Jira issue

- **Issue key:** <!-- Required for planned work, e.g. EVF-12. Replace with your actual Jira project key. -->
- **Issue link:** <!-- Paste the direct Jira issue URL. -->
- **Roadmap task:** <!-- Task 1–12, if relevant; otherwise N/A. -->
- **Related ADR:** <!-- Link or N/A. -->

<!-- Recommended PR title: type(scope): short summary [EVF-12] -->
<!-- EVF-12 is an example key, not the project's confirmed Jira key. -->

## Change type

- [ ] Documentation / architecture proposal
- [ ] Feature
- [ ] Bug fix
- [ ] Refactoring
- [ ] Tests / CI / infrastructure

## Design notes

<!-- Explain important choices and trade-offs. For documentation-only PRs, describe the decision being clarified. -->

- **Domain boundaries / invariants:** <!-- changed, unchanged, or N/A -->
- **Events / schemas / replay compatibility:** <!-- changed, unchanged, or N/A -->
- **Transactions / projections / concurrency:** <!-- changed, unchanged, or N/A -->
- **Alternatives or trade-offs considered:** <!-- concise explanation or N/A -->

## Validation

<!-- Report only checks actually performed. Use N/A with a reason where appropriate. -->

| Check | Result / evidence |
| --- | --- |
| Unit tests | Not run / N/A / command + result |
| PostgreSQL integration tests | Not run / N/A / command + result |
| Manual or documentation review | What was verified? |

## Review checklist

- [ ] PR scope matches the linked Jira issue and its acceptance criteria.
- [ ] Commit messages reference the correct Jira key.
- [ ] Changes match the stated scope; no unrelated complexity was introduced.
- [ ] The README, roadmap, and ADRs remain consistent with actual implementation status.
- [ ] Important architectural changes are documented and require project-owner approval.
- [ ] Domain rules and transaction boundaries are reviewed where relevant (or N/A).
- [ ] Existing event contracts and replay behavior are preserved or explicitly addressed (or N/A).
- [ ] No confidential material, proprietary source code, secrets, or real customer data are included.
- [ ] Test evidence is accurate; no unverified behavior is described as implemented.

## Risks, limitations, and follow-up

<!-- Known risks, migration/rollback considerations, deferred work, or 'None identified'. -->
