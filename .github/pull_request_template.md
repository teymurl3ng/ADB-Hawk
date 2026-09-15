## Phase

Phase N from `PHASES.md`: <!-- one line on what this PR delivers inside that phase -->

## Checklist

- [ ] `scripts/verify.ps1` is green end to end (full output linked or pasted; never a partial run — AGENTS.md R-12)
- [ ] This phase's acceptance criteria from `PHASES.md` are each demonstrably met (list them)
- [ ] Every gate touched by this change was deliberately violated once and observed failing ("break it on purpose" record below — not merged)
- [ ] No new dependency, timer, component variant, or user-visible string without its paperwork (research note + `ENGINEERING.md §15`/`§21` edit in the same change — R-09, R-05, R-02, R-15)
- [ ] `research/` refreshed for anything learned about an external API (R-10)
- [ ] `DESIGN.md` untouched; `docs/DESIGN.sha256` unchanged (or accompanied by an explicit design-authority decision note — R-01)
- [ ] Tests shipped with the change (R-11)

## Break-it-on-purpose record

| Gate | How it was violated | Observed failure |
|---|---|---|
| | | |

## Not done / unsure

<!-- What this PR deliberately does not do, and anything you were unsure about. -->
