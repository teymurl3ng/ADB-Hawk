# Regenerate ui/src/contract/snapshot.ts from the C# records and fail if the
# committed output drifts (ENGINEERING.md 4.2). The generated contract does not
# exist until Phase 3 (Phase 2 hand-writes it); until then this is a documented no-op.
[CmdletBinding()]
param()

Write-Host 'gen-contract: no generated contract exists until Phase 3 (see PHASES.md Phase 3 / ENGINEERING.md 4.2); nothing to regenerate.'
