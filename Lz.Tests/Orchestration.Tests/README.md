# Orchestration.Tests — deployment-lifecycle tests

Home of the **teardown–redeploy drill** (`TeardownRedeployTests`) and its safety
plumbing. Plan/decisions/inventory: [TEARDOWN-REDEPLOY-PUNCHLIST.md](TEARDOWN-REDEPLOY-PUNCHLIST.md).

## ⚠ What the drill does

Destroys and redeploys the **live dev system** (`lzm`/`dev`, `lazymagicdev.click`)
through the installed `lz` CLI, asserting that nothing Pulumi-managed lingers after
destroy (no orphans, no Secrets Manager tombstones), that the persistent layer
(ECR, DynamoDB tables, webapp buckets, SSM, state backend) survives untouched, and
that the redeploy comes back healthy (verify + previews + HTTP smoke). It
interrogates live state first and **always ends in the beginning state**.

- Full cycle ≈ **60–120 min** (CloudFront delete alone is 20–40).
- **All Cognito users in both pools are lost** each enabled run (UI-Tests self-heal;
  manual dev accounts do not).

## Safety rails

1. **Dev-only guard** (`DevEnvironmentGuard`) — hard-fails before the first lz call
   unless the resolved env, the systemconfig filename, and the working-copy path all
   say dev. Unit-tested in the default fast suite.
2. **Opt-in destructive phase** — without `LZ_LIFECYCLE_ENABLE=1` the test only runs
   the read-only interrogation (`lz verify --json`), reports the branch it would
   take, and skips.
3. **Integration trait** — `dotnet test --filter Category=Integration` to include it;
   plain CI filters it out with `--filter Category!=Integration`.

## Running

```bash
aws sso login --profile lzm-dev          # SSO session required
dotnet build Deploy                       # plugin must be discoverable
cd lz
# read-only interrogation only:
dotnet test Lz.Tests/Lz.Tests.csproj --filter Category=Integration
# the real drill (supervised!):
LZ_LIFECYCLE_ENABLE=1 dotnet test Lz.Tests/Lz.Tests.csproj --filter Category=Integration
```

| Env var | Default | Meaning |
|---|---|---|
| `LZ_LIFECYCLE_ENABLE` | unset | `1` = actually run the destructive drill |
| `LZ_LIFECYCLE_TENANT` | `mp` | tenant key to destroy/redeploy |

Per-step lz output lands in `artifacts/{timestamp}/NN-{label}.log` (gitignored).
Preconditions (lz on PATH, plugin built, SSO alive) **skip** gracefully; safety
violations and drill failures **fail** loudly.

## Files

| File | Role |
|---|---|
| `DevEnvironmentGuard(.Tests).cs` | dev-only gate + its fast-suite tests |
| `LifecycleHarness.cs` | repo-root discovery, `lz` subprocess runner, verify-JSON snapshots |
| `TeardownRedeployTests.cs` | the drill: guard → preconditions → interrogate → branch → phases |
