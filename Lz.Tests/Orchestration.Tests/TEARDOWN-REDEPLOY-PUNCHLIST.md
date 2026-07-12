# Teardown–Redeploy Lifecycle Test — Punchlist

**Objective:** a full-system destroy → redeploy drill, driven by the `lz` tool, proving that
no deployed resource lingers after a destroy that would block a subsequent deploy. The test
interrogates the live system to determine its starting state (deployed or not) and always
ends in that beginning state.

**Target:** the live dev system — SystemKey `lzm`, Tenant `mp`, Env `dev`, profile
`lzm-dev`, us-west-2, `lazymagicdev.click`, topology `lambda-cognito-dynamodb`.
Stacks: foundation `lzm-dev` + tenant `lzm-mp-dev` (Pulumi project `lz-lzm`).

**Decisions (2026-07-12):**

| Question | Decision |
|---|---|
| "lz only" strictness | Both: add a **live-verify command to lz** (A1) *and* allow **read-only AWS SDK checks** in the test — required post-destroy, when Pulumi state is gone |
| Destroy scope | **Pulumi stacks only** (`destroytenant` → `destroysystem`); the persistent imperative layer must **survive** and be assertable via a new **persistent-items listing** (part of A1) |
| End-state verification | `lz status` + `preview* --fail-on-replace` + HTTP smoke (UI-Tests stay a separate suite) |
| Harness | **xUnit** here in `lz\Lz.Tests\Orchestration.Tests\`, `[Trait("Category","Integration")]`, driving the `lz` CLI as a subprocess |

**Duration budget:** ~60–120 min per full cycle (CloudFront delete alone 20–40 min;
ACM re-issuance, Cognito custom-domain recreate add more). Plan timeouts accordingly.

---

## Phase A — lz tool prerequisites

- [x] **A1. Live-AWS interrogation command** — shipped as **`lz verify`** (lz **0.10.40**
  after a 23-finding adversarial review pass: tenant-effective Profile/Region for tenant
  checks, per-function CF checks, no silent default-credential fallback, `--expect`
  verdict computed over unfiltered results, binary-compatible `ResolveEnvironment`
  overloads, `--yes` refuses runtime-discovered multi-target sets):
  - Enumerates the expected resources by naming convention from systemconfig/tenantconfig
    (no Pulumi state involved — works post-destroy). `Lz.Aws/Verification/AwsLiveVerifier.cs`.
  - Classifies **`stack`** vs **`persistent`**; `--scope stack|persistent|all`
    (`--scope persistent` = the persistent-items listing).
  - `--json` machine output; `--expect deployed|destroyed` sets the exit code.
  - Read-only; validated live against dev: `--expect deployed` → MET, 26 stack + 19
    persistent present, 0 errors.
  - Live finding: the `lzm` system DynamoDB table **does not exist in dev at all** —
    "never created by lz on this topology" is confirmed against reality; the persistent
    baseline-diff approach in the test handles it (absent before = absent after is OK).
- [x] **A2. Non-interactive destroy** — `--yes` added to `destroysystem`/`destroytenant`.
- [x] **A3. `lz status` create-on-probe fixed** — status AND destroy now use
  `SelectStackReadOnly` (`SelectStackAsync` + `StackNotFoundException` → "not deployed");
  probing or destroying a never-deployed system no longer creates an empty stack.
- [ ] **A4. (Optional) `--refresh` on destroy commands** — destroy currently runs from
  possibly-stale state (`DestroyStackAsync` never refreshes; deploy always does).
  Mitigated in-test by `previewsystem --refresh` between the two destroys (B4).
- [x] **A6. (Found en route) CLI exit codes fixed** — `Main` returned `InvokeAsync`'s
  result, which made every `Environment.ExitCode = 1/2` assignment in the CLI dead
  (verified empirically: error paths exited 0). Main now honors `Environment.ExitCode`
  when `InvokeAsync` returns 0 — this fixes exit codes for ALL commands, not just verify.
- [x] **A5b done: repacked as lz 0.10.40** (bump → build → pack ×5 → Deploy re-restore;
  installed runner resolves it). Note for future lz edits: NuGet caches by version —
  always bump, never overwrite a packed version in place.
- [x] **A5a. Pull the lz repo** — done 2026-07-12 (now at `a227ab9`, lz **0.10.36**).
  The pull brought in `424a106`: on the lambda topology, `deploytenant` now re-ensures
  the tenant/BFF/subtenant DynamoDB tables (`AwsEcsExpressPostDeployAction` with empty
  services) **and runs `VerifyApexAliasAsync`** — a built-in post-redeploy apex check.
  The `lzm` **system table remains never-created** on this topology (foundation
  post-deploy action still only invoked from `SharedDeployment`) — do-not-touch stands.
- [ ] **A5b. Packaging** — live behavior comes from the packed
  `lz\Packages\Lz.Cli.0.10.36.nupkg`, not `Lz.Cli` source: A1–A4 require version bump +
  repack + feed publish before the installed `lz` picks them up.

## Phase B — test implementation (this folder)

- [x] **B0 shipped**: `DevEnvironmentGuard.cs` + 25 unit tests (fast suite).
- [x] **B1–B3 shipped**: `LifecycleHarness.cs` (repo root, lz subprocess runner with
  per-step artifact logs, `lz verify --json` snapshots) + `TeardownRedeployTests.cs`
  (`[Trait("Category","Integration")]`, guard → preconditions → interrogation → branch;
  full destroy/redeploy phases implemented, gated by **`LZ_LIFECYCLE_ENABLE=1`** —
  without it the test does the read-only interrogation and reports the branch it would
  take). Read-only path validated live (found dev DEPLOYED, 26/0). B4–B9 logic is in
  the two phase methods; first supervised enabled run still pending.
- [ ] **B0. Dev-only guard (safety — runs before anything else).** The lifecycle test
  may ONLY run against a dev environment — never test, never prod. Multi-signal check,
  all of which must agree before any lz invocation (including the read-only
  interrogation):
  1. the resolved environment (lz's folder-hierarchy auto-detect: `_Dev_`→dev) is `dev`;
  2. the systemconfig consumed is `systemconfig.{sk}.dev.yaml` and its filename-derived
     `Environment` is `dev`;
  3. the working-copy path contains no `_Test_`/`_Prod_` segment.
  On any mismatch the test **fails loudly** (assert-fail, not skip) naming the offending
  signal — an attempted run in test/prod is operator error and must be visible. The
  guard is a small pure class (`DevEnvironmentGuard`) with its own unit tests (which run
  in the default fast suite, not just the Integration filter).
- [ ] **B1. Scaffold** — `LifecycleTests.cs` (`Lz.Tests.Orchestration.Tests` namespace),
  `[Trait("Category","Integration")]` so plain `dotnet test` stays fast
  (`--filter Category=Integration` opts in); `TEST_*` env knobs with dev defaults;
  per-phase log capture to a run folder; suite README.
- [ ] **B2. Preconditions gate** (graceful **skip**, never fail — house convention):
  SSO session valid (`sts:GetCallerIdentity` via profile `lzm-dev`); Deploy plugin built
  (`Deploy\bin\...\Deploy.dll` — every `deploy*/destroy*/status` command throws without
  it); cwd inside `_Dev_MagicPets` (env auto-detect); log `lz --version` (runner + cli +
  plugin) into the artifacts.
- [ ] **B3. Starting-state interrogation → branch** (via A1 `--live --json`):
  - **Deployed** → destroy → verify-clean → redeploy → verify-healthy. Ends deployed. ✔
  - **Not deployed** → deploy → verify-healthy → destroy → verify-clean. Ends absent. ✔
  - **Partial/indeterminate** (some stack resources present, some missing) → fail fast
    with a diagnostic dump; do not "repair" automatically.
- [ ] **B4. Destroy sequence** — `lz destroytenant --tenantkey mp` (wait: distribution
  disable+delete) → **refresh foundation state** (`previewsystem --refresh`; drops
  Route53 records the tenant destroy already removed from the foundation's stale state)
  → `lz destroysystem`.
- [ ] **B5. Post-destroy assertions** (A1 `--live` + direct SDK spot-checks):
  - Every **must-vanish** item absent — including *no Secrets Manager deletion
    tombstone* for `lzm/mp` (`RecoveryWindowInDays=0` path) and *no lingering
    user-pool custom domain* `auth.lazymagicdev.click`.
  - Every **must-survive** item intact — most critically the ECR image
    `lzm-496a-bd90-dev-mp-apphost:latest` (deploytenant's pre-flight requires it;
    without it redeploy needs Docker + `deploycontainer`).
  - Pulumi backend: empty stack files remaining in `lzm-dev-pulumi-state-496a-bd90`
    are the **expected** clean end-state (no lz command exposes `stack rm`).
- [ ] **B6. Redeploy sequence** — `lz deploysystem` → `lz deploytenant --tenantkey mp`
  (re-ensures tenant/BFF/subtenant DynamoDB tables idempotently + provisions subtenant
  buckets + KVS refresh) → **`lz deployassets`** (required: the three `ForceDestroy`
  assets buckets die with the tenant stack; content only comes back from `/Tenancies/`).
  `deploycontainer`/`deploywebapp`/`deploystaticsite` are *not* run (survivors).
- [ ] **B7. End-state verification** — `lz status` green for both stacks;
  `lz previewsystem --fail-on-replace` and `lz previewtenant --fail-on-replace` exit 0
  (steady-state: no drift, no pending replaces); HTTP smoke: apex `/` 200,
  `/config` advertises `tenantauth` + `consumerauth`, `/bff/login` 302s to a Managed
  Login host that renders a sign-in form (not "Login pages unavailable"),
  subtenant host (`uptown.lazymagicdev.click`) serves the landing page.
- [ ] **B8. Named blocker regressions** (each a discrete assertion, so a failure names
  its class): secret-tombstone redeploy blocker; Cognito custom-domain release/recreate;
  us-east-1 + regional ACM cert re-issuance & Route53 validation (`AllowOverwrite`);
  CloudFront named-resource conflicts (cache policy `lzm-mp-cache-host-keyed-dev`, OAC
  `lzm-mp-oac`, KVS `lzm-mp-kvs`, 6 functions); `deploytenant` idempotency over the
  persistent DynamoDB tables; the **Route53 cross-stack double-ownership experiment**
  (apex A record + ACM validation CNAMEs live in both stacks' state — first-ever full
  two-stack destroy; B4's refresh is the mitigation, capture the actual behavior).
- [ ] **B9. Reporting** — per-phase wall-clock, per-assertion pass/fail, full lz output
  logs as artifacts; final summary table.
- [ ] **B10. Docs** — README in this folder (destructive! ~60–120 min; how to run;
  `TEST_*` knobs); register the suite alongside `UI-Tests\tests-index.md` conventions;
  fix the stale root `CLAUDE.md` claim that "no test projects are configured".

## Resource inventory (topology `lambda-cognito-dynamodb`, lzm/mp/dev)

### Must vanish after destroy (Pulumi-managed)

| Resource | Name |
|---|---|
| CloudFront distribution | aliases `lazymagicdev.click` + `*.lazymagicdev.click` |
| CloudFront functions (6) | request/authconfig/explore/auth/auth-callback/response |
| CloudFront KeyValueStore | `lzm-mp-kvs` |
| Origin Access Control / cache policy | `lzm-mp-oac` / `lzm-mp-cache-host-keyed-dev` |
| ACM certs (4) | regional apex+wildcard; us-east-1 CDN; us-east-1 × 2 Cognito domains |
| Cognito user pools (2) | `lzm-496a-bd90-dev-tenantauth`, `-consumerauth` (+ clients, BFF clients, identity pools, groups, custom domains `auth.lazymagicdev.click`, `auth-consumerauth.…`) |
| CloudWatch log groups | `/aws/cognito/lzm-dev-{tenantauth,consumerauth}` |
| Secrets Manager secret | `lzm/mp` (RecoveryWindow=0 → **no tombstone**) |
| Lambda + Function URL + permissions | `lzm-mp-apphost` |
| IAM exec role | `lzm-mp-apphost-exec` |
| Route53 records | apex A-alias, `*.` wildcard alias, ACM validation CNAMEs |
| S3 assets buckets (3, ForceDestroy) | `lzm---assets-496a-bd90`, `lzm-mp--assets-496a-bd90`, `lzm-mp-496a-bd90-dev-assets` |

### Must survive destroy (persistent imperative layer)

| Resource | Name |
|---|---|
| ECR repo + `:latest` image | `lzm-496a-bd90-dev-mp-apphost` (**redeploy-critical**) |
| DynamoDB tables | `lzm` (never recreated by lz — do not touch), `lzm_mp`, `lzm_mp_bff`, `lzm_mp_cbff`, `lzm_mp_uptown`, `lzm_mp_downtown` |
| S3 buckets | subtenant assets `lzm-mp-{uptown,downtown}-assets-496a-bd90`; webapp `lzm---webapp-{storeapp,adminapp,consumerapp}-496a-bd90`; explore/static-site buckets |
| SSM parameter | `/lzm/dev/bff/dataprotection` |
| CloudWatch log group | `/aws/lambda/lzm-mp-apphost` (runtime-created) |
| Pulumi state backend | S3 `lzm-dev-pulumi-state-496a-bd90` + KMS `alias/lzm-dev-pulumi-key-496a-bd90` |
| Route53 hosted zone | `lazymagicdev.click` |

## Drill run #1 — 2026-07-12 (artifacts `20260712-090019`)

First live enabled run, from DEPLOYED. **The destroy phase was clean and the test
caught real residue** — it failed exactly as designed:

| Step | Result | Elapsed |
|---|---|---|
| interrogate | DEPLOYED (31 stack / 19 persistent) | 2s |
| `destroytenant --tenantkey mp --yes` | ✅ exit 0 | **4m34s** |
| `previewsystem --refresh` | ✅ exit 0 | 48s |
| `destroysystem --yes` | ✅ exit 0 | **1m42s** |
| post-destroy verify | ❌ 1 stack resource still present | 1m04s |

**Experiment outcomes (B8):**
- **Route53 cross-stack double-ownership: NO failure.** The full two-stack destroy
  (first ever) completed cleanly — no `DependencyViolation`, no delete-not-found
  errors; the refresh-between-destroys mitigation worked. Apex/wildcard/validation
  records all gone.
- **Secrets tombstone: PASSED** — 0 tombstoned (the `RecoveryWindowInDays=0` path holds).
- **Persistent layer: 19/19 intact** (ECR image, all DynamoDB tables, webapp/subtenant
  buckets, SSM, state backend).
- **Timing reality check:** full two-stack teardown ≈ **7 minutes**, not the estimated
  60–120 (the CloudFront distribution delete was fast). Budget accordingly.

**Residue found (the test working as intended):** a **legacy orphan ACM cert** in
us-east-1 for `lazymagicdev.click` — created **2024-03-18** (pre-lz era), untagged,
`InUseBy=[]` — a duplicate of the stack-managed CDN cert (which WAS correctly
deleted). Invisible until the strict post-destroy sweep. **Deleted 2026-07-12 with
user approval** (`f19bd87a-1f5f-46b3-9acc-5ac2ee82b7df`). Baseline is now clean.

**Second find — a genuine FROM-SCRATCH redeploy blocker (the drill's headline):**
the restore's `deploytenant` failed creating the CloudFront distribution with
`InvalidArgument: The parameter Origin S3 Origins can only use the following managed
request policies: CORS-CustomOrigin, CORS-S3Origin, UserAgentRefererHeaders`. Root
cause: the `/authentication/*` behavior targeted the **S3** assets origin while
carrying the **AllViewerExceptHostHeader** origin-request policy ("same as /auth/*
for consistency") — legal-looking in code, rejected by CreateDistribution. The live
distribution never exercised this because in-place updates never recreate it; only
a real destroy→redeploy reaches CreateDistribution. **Fixed in lz 0.10.41**
(`AwsEcsExpressCloudFrontComponent.cs`: `/authentication/*` → CORS-S3Origin); other
CDN components audited — no other S3-origin behavior carries an AllViewer policy.
This one finding justifies the drill: the config was un-deployable from scratch and
nobody knew.

## Drill run #2 — 2026-07-12 (artifacts `20260712-092828`)

From DEPLOYED with the clean baseline + lz 0.10.41. **The destroy-phase objective
passed end-to-end**: teardown ~8 min (destroytenant 5m00 / refresh 19s /
destroysystem 1m43), post-destroy sweep **CLEAN** — 31/31 stack gone, 0 tombstones,
19/19 persistent intact. The drill then advanced into redeploy and found blocker #3:

**Third find — Cognito custom-domain release lag.** `deploysystem` failed
`CreateUserPoolDomain` (400) for BOTH `auth.` and `auth-consumerauth.` domains ~5 min
after the destroy: Cognito custom domains are internally CloudFront-backed and the
name stays "taken" until AWS releases the internal distribution (~15 min, not
queryable via any API). Run #1's manual restore dodged it only because diagnosis
added ~40 min of accidental cool-down. **Fixed in lz 0.10.42**: `PulumiUpAsync`
(foundation) retries the refresh+up on exactly this error, up to ~30 min at 2-min
spacing — `pulumi up` is resumable, so each retry continues from partial state.

## Drill run #3 — 2026-07-12 (artifacts `20260712-095406`) — ✅ **GREEN, END TO END**

With all three finds fixed (orphan cert deleted, lz 0.10.41 CDN config, lz 0.10.42
domain retry): **Passed, 25m42s total**, dev ends deployed and healthy.

| Phase | Elapsed |
|---|---|
| interrogate (DEPLOYED 31/0) | 1s |
| destroytenant | 4m48s |
| previewsystem --refresh | 19s |
| destroysystem | 1m42s |
| post-destroy verify → **CLEAN** (0 lingering, 0 tombstoned, persistent 19/19) | 1s |
| deploysystem (**domain retry fired 3× — window measured ~9–10 min**) | 12m45s |
| deploytenant (fresh distribution OK) | 4m52s |
| deployassets | 25s |
| verify --expect deployed → MET | 22s |
| previews --fail-on-replace → "no changes" ×2 | 21s |
| HTTP smoke (apex + /config pools) | pass |

The objective is met and repeatable: destroy leaves nothing that lingers or blocks,
the persistent layer survives, redeploy restores a verified-healthy system, and the
test always ends in the beginning state.

## Accepted risks / signoffs

- Destroys the **live dev environment**; downtime for the full cycle.
- **Irreversible per run:** all Cognito users in both pools (UI-Tests self-heals — it
  admin-creates its own user and resolves pool IDs from `/config`; any manually created
  dev accounts are lost), CloudFront distribution ID/domain, pool IDs (stale doc refs
  e.g. `Platform/ConsumerAppBffPlan.md`), origin-verify secret.
- The Route53 cross-stack destroy behavior is a genuine unknown until first run (B8).
- `deletetestusers` exists for Cognito test-user sweeps if runs abort mid-cycle.
