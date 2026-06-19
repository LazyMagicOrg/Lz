# Target Isolation — Implementation Plan

Companion to [`TargetIsolation.md`](TargetIsolation.md). That document explains
*why* and picks the approach (derived classes in `Lz.Aws` + YAML type mapping
via `IConfigExtensions`). This document lays out *how* we execute on it: phase
order, files touched, tests, plugin impact, and risks.

## Status

Phases 0–4 **complete**. Phase 5 (generic orchestration when a second platform
lands) remains deferred. See per-phase sections below for what landed and where.
The final state is captured as the "resolution status" section of
[`TargetIsolation.md`](TargetIsolation.md).

## Objective and scope

**Goal.** Eliminate AWS vocabulary from `Lz.Core` using the
`IConfigExtensions` mechanism already plumbed. End state: `Lz.Core` types
speak only in shapes; `Lz.Aws` owns every AWS name, field, and flow branch.

**Non-goals (explicit).**

- **Pulumi-independence (Tier 3) is out of scope.** `Pulumi.Output<T>` stays
  in output interfaces. We are not trying to support a second IaC engine.
- **Orchestration flow-object refactor (original Pass 3) is deferred.** Only
  worth doing when a second real platform (Azure) is a committed target.

**Test subject.** `BCProjNew` (the `Deploy/` plugin plus its
`Tenancies/*/*.yaml` configs). Every phase must leave `lz` commands working
end-to-end against BCProjNew.

## Ordering principles

1. Every phase leaves `Lz.Tests` green and `lz` CLI working end-to-end
   against BCProjNew. No big-bang.
2. Plumbing fixes come before first real use. First real use validates
   the pipeline end-to-end before we scale it.
3. Cosmetic renames land in their own commits so review diffs stay
   readable.
4. Existing user-authored YAML keeps working without edits. If a shape
   change forces a YAML edit, it's announced in the commit body and
   carries a validator message naming the old key.
5. Each `Lz.*` phase has a paired BCProjNew/Deploy update committed in
   the same session (separate commits, BCProjNew repo).

## Plugin compatibility (cross-cutting)

Systems using this tool can define a `Deploy` plugin project (see
`BCProjNew/Deploy/`) that implements `ILzPlugin` and can register custom
CLI commands, reference AWS SDKs directly, and override tool behaviour.

**Contract.** `ILzPlugin` itself does not change. Plugins are explicitly
permitted to be platform-aware — BCPlugin uses the AWS SDK for CloudFront
KVS and DynamoDB today.

**Breaking surface when fields move to derived configs.** When Phase 3
moves fields like `SystemConfig.AppRunner`, `SystemConfig.ECS`,
`SystemConfig.State`, `SharedConfig.Keycloak`, etc. onto `AwsSystemConfig`
et al., plugins that read those fields break at compile time.

**Resolution pattern.** Plugins cast the base config to the derived
type, using the same `is`/direct-cast idiom `Lz.Aws` internal components
use:

```csharp
// In a plugin command handler:
var config = ConfigLoader.LoadSystemConfig(path);
var aws = (AwsSystemConfig)config;         // plugin knows it targets AWS
var port = aws.AppRunner?.Port ?? 8080;
```

Plugins already reference `Lz.Aws` transitively (via `Lz.Core` + AWS SDK
packages); once Phase 3 ships, plugin `.csproj` files add a direct
`ProjectReference` / `PackageReference` to `Lz.Aws` if not already
present.

**Documentation.** At the end of Phase 3, add a short "Writing a plugin
for AWS" section to the top-level README or a dedicated `Design/Plugins.md`
covering the cast pattern and when to use `ConfigLoader.RegisterExtensions`
vs. just casting.

---

## Phase 0 — Close the "Known caveat" (prereq, ~0.5 day) — ✅ complete

Before a second platform can land, `RegisterExtensions` needs to be
platform-gated. Today `WithTypeMapping<T, U>()` is unconditional and
the last registration silently wins.

### Changes

- [Lz.Core/Config/IConfigExtensions.cs](../Lz.Core/Config/IConfigExtensions.cs)
  — add `string Platform { get; }` so `ConfigLoader` can filter without
  string-matching class names.
- [Lz.Core/Config/ConfigLoader.cs](../Lz.Core/Config/ConfigLoader.cs) —
  in `BuildDeserializer`, do a lightweight preliminary scan to determine
  the active platform (read `platform:` key from the YAML being loaded,
  or fall back to `SystemConfig.Platform` from a first trivial parse),
  then only run `Configure` on matching extensions.
- [Lz.Aws/Config/AwsConfigExtensions.cs](../Lz.Aws/Config/AwsConfigExtensions.cs)
  — implement `Platform => "aws"`.
- [Lz.Cli/Program.cs:49](../Lz.Cli/Program.cs:49) — keep the registration
  call, drop any "AWS is the only platform" comment.

### Tests

Add `Lz.Tests/Config/ConfigLoaderExtensionsTests.cs`:

- Register two fake `IConfigExtensions` (`Platform = "aws"`, `Platform =
  "azure"`) that each map `AuthConfigEntry` to a different derived type.
  Assert only the matching one materialises based on a YAML whose
  `platform:` field selects it.
- Assert that when no extension matches, the base `AuthConfigEntry`
  materialises and no exception is thrown.

### BCProjNew update

None. The plugin doesn't touch `IConfigExtensions`.

### Why first

Every subsequent phase adds `WithTypeMapping` calls. Doing it on a broken
foundation means rework when Azure eventually ships. Also answers an open
question in `TargetIsolation.md` §"Known caveat".

---

## Phase 1 — Cosmetic scrub (~0.5 day) — ✅ complete

Pure rename / comment cleanup. Zero structural change. One commit on
each side.

### Lz.Core renames

- [Lz.Core/Definitions/SystemDefinition.cs:68-75](../Lz.Core/Definitions/SystemDefinition.cs:68)
  — `UseCognito(string[] pools)` → `UseAuth(string[] realms)`. Drop the
  `Provider = "cognito"` / `"keycloak"` literals from the base class.
  `AuthDefinition.Provider` stays an opaque string (populated by the
  platform, not by core).
  - Keep `UseKeycloak` **or** fold it into `UseAuth` — decision deferred
    until we read BCProjNew's usage. BCProjNew only calls `UseCognito`,
    so `UseAuth` is sufficient for the test subject.
- [Lz.Core/Interfaces/IPlatformFactory.cs](../Lz.Core/Interfaces/IPlatformFactory.cs)
  — rewrite every doc comment to speak in shapes. No mentions of Cognito,
  Route 53, Lambda, ECR, SES, CloudFront, ECS, AppRunner. Method names
  unchanged this phase (renamed in Phase 4).
- [Lz.Core/Interfaces/Outputs/IAuthPoolOutputs.cs](../Lz.Core/Interfaces/Outputs/IAuthPoolOutputs.cs)
  — strip "e.g., Cognito user pool" comments.
- [Lz.Core/Interfaces/Outputs/ISeedTaskOutputs.cs](../Lz.Core/Interfaces/Outputs/ISeedTaskOutputs.cs)
  — rename `EcrRepositoryUrl` → `ContainerImageRepositoryUrl`. Real API
  break; update `Lz.Aws` call sites in the same commit.

### Comment sweep

```
rg -n "Cognito|Keycloak|Tailscale|CloudFront|Route ?53|ECR|Lambda|SES|App ?Runner|ECS" Lz.Core/
```

Each hit in `Lz.Core/` either deleted (redundant) or reworded to the
shape.

### BCProjNew update (required)

- [BCSystem.cs:41](../../BCProjNew/Deploy/BCSystem.cs:41) —
  `UseCognito(...)` → `UseAuth(...)`.

### Out of scope this phase

Renaming `ECS:` / `AppRunner:` YAML sections — those move to derived
configs in Phase 3 where they belong.

---

## Phase 2 — First real derived config type: `AwsAuthConfigEntry` (~1 day) — ✅ complete

This is what the Cognito hardening work wants. Doing it first exercises
the whole `IConfigExtensions` pipeline on a small, contained surface.

### Changes

- `Lz.Aws/Config/AwsAuthConfigEntry.cs` — new class deriving from
  `Lz.Core.Config.AuthConfigEntry`. Fields per
  `Platform/CognitoHardeningPlan.md` (in `BCProjNew`): `AdvancedSecurityMode`,
  `Groups` (as a typed list, not dictionary), `PasswordPolicy`, any
  Cognito-specific MFA fields.
- [Lz.Aws/Config/AwsConfigExtensions.cs:25](../Lz.Aws/Config/AwsConfigExtensions.cs:25)
  — add `builder.WithTypeMapping<AuthConfigEntry, AwsAuthConfigEntry>();`.
- AWS auth component consumers — cast via `if (entry is AwsAuthConfigEntry
  aws) { … }` where Cognito-specific fields are read.

### Tests

`Lz.Tests/Config/AwsAuthConfigEntryRoundTripTests.cs`:

- Feed a YAML fragment with Cognito-specific keys through `ConfigLoader`.
- Assert the materialised entries are `AwsAuthConfigEntry`.
- Assert Cognito-specific fields populate correctly.

### BCProjNew update

Populate `AuthConfigs:` in `systemconfig.bcs.dev.yaml` (or a test fixture)
with Cognito-specific fields and run `lz updatekvs` end-to-end to confirm
the cast works through the plugin path.

---

## Phase 3 — Derive the three big config types + move orchestration (Path C) — ✅ complete

**Note:** The original Phase 3 assumed `SharedDeployment`/`SystemDeployment`
could stay in `Lz.Core`. Auditing found they're deeply AWS-ECS-Keycloak
shaped — they construct `EcsConfig` directly, read `config.Keycloak`,
etc. Rather than introduce virtual hooks or leave Tier 1 half-done, we
folded the original Phase 5 work into Phase 3 as "Path C": move both
orchestrators to `Lz.Aws/Orchestration/` first, *then* move the config
types. Listed in Phase 3 below as additional work.

Move AWS-shaped typed sections out of `Lz.Core` into `Lz.Aws` derived
classes. This is the biggest single net-LOC change in the plan and touches
every call site that reads these fields — both inside `Lz.Aws` and in the
BCProjNew plugin.

### New types in `Lz.Aws/Config/`

```
AwsSystemConfig   : SystemConfig    // holds EcsConfig ECS, AppRunnerConfig AppRunner,
                                    // StateConfig State, SharedSecretArn, SharedKmsKeyArn,
                                    // SharedProfile, SharedRegion, TrustedAccountIds,
                                    // CentralAuthDomain, SeedDataConfig SeedData

AwsTenantConfig   : TenantConfig    // holds EcsConfig ECS, AppRunnerConfig AppRunner,
                                    // AcmCertificateArn, HostedZoneId,
                                    // SharedSecretArn/KmsKeyArn, CentralAuthDomain

AwsSharedConfig   : SharedConfig    // holds SharedKeycloakConfig Keycloak,
                                    // TailscaleInstanceType, TailscaleDesiredCapacity,
                                    // TrustedAccountIds, SeedDataConfig SeedData
```

### Types that move wholesale from `Lz.Core/Config/` to `Lz.Aws/Config/`

`EcsConfig`, `AppRunnerConfig`, `KeycloakSeedConfig`, `SharedKeycloakConfig`,
`StateConfig`, `SeedDataConfig`.

### What stays in `Lz.Core/Config/`

Generic shapes only: `SystemKey`, `Environment`, `Platform`, `Topology`,
`SystemSuffix`, `Profile`, `Region`, `SystemDomain`, `DefaultTenant`,
`VpcCidr`, `AdminAuth`, `AdminEmail`, `BehaviorsConfig`, `CdnConfig`
(see risk 1 — audit first), `AuthConfigs`, `SecretsManagerConfig`,
`IntegrationsConfig`, runtime logging configs.

### YAML compatibility

`WithTypeMapping<SystemConfig, AwsSystemConfig>()` means user YAML is
unchanged — same keys, same nesting. The derived type picks up `ECS:` /
`AppRunner:` / `SharedSecretArn:` because those fields live on the
derived class and YamlDotNet's PascalCase deserializer finds them there.

### Call-site update strategy

1. Extract the "needs AWS fields" call sites by grep:
   ```
   rg -n 'config\.ECS\b|config\.AppRunner\b|config\.SharedSecretArn|config\.TrustedAccountIds|config\.CentralAuthDomain|config\.State\b' Lz.*/
   ```
   Expect ~50–100 hits, almost all in `Lz.Aws`.
2. Introduce a small helper in `Lz.Aws` at the boundary where AWS code
   receives the base type:
   ```csharp
   internal static class AwsConfigCast
   {
       public static AwsSystemConfig Aws(this SystemConfig c) => (AwsSystemConfig)c;
       public static AwsTenantConfig Aws(this TenantConfig c) => (AwsTenantConfig)c;
       public static AwsSharedConfig Aws(this SharedConfig c) => (AwsSharedConfig)c;
   }
   ```
   Then `config.Aws().AppRunner` at each site, rather than a local cast.
3. Any `Lz.Core` code that currently reads these AWS-shaped fields (per
   audit: `ConfigMerger.GetEffectiveEcs`, `ConfigValidator` Cognito/Keycloak
   messages) either moves to `Lz.Aws` or gets a hook: core exposes a
   `protected virtual` method on the base class that derived AWS types
   override. Proof-of-concept required (see risk 2).

### BCProjNew update (required, same session)

- [BCSystem.cs:27-29](../../BCProjNew/Deploy/BCSystem.cs:27) —
  `config.AppRunner?.Port` → `((AwsSystemConfig)config).AppRunner?.Port`.
  Likewise `HealthCheckPath`.
- [BCPlugin.cs:228-233](../../BCProjNew/Deploy/BCPlugin.cs:228) —
  `config.State?.Backend/SecretsProvider` → same cast pattern.
- `Deploy.csproj` — add `ProjectReference` (or `PackageReference`) to
  `Lz.Aws` if not already present transitively.

### Tests

- Round-trip tests per derived type:
  `AwsSystemConfigRoundTripTests`, `AwsTenantConfigRoundTripTests`,
  `AwsSharedConfigRoundTripTests`.
- Run BCProjNew's `lz deploysystem --dry-run` equivalent against a
  fixture config in CI (if CI is wired up; otherwise smoke-test locally).

### Commit cadence

Three commits in `Lz.*`, one per derived type (`AwsSystemConfig`,
`AwsTenantConfig`, `AwsSharedConfig`), keeping compile green on each.
Mirror commits in BCProjNew, one per phase boundary.

### Risk

`ConfigMerger` is the hardest piece. Merge semantics depend on field
shape. Need to read it carefully before committing: if merge logic is
entangled with runtime Secrets Manager helpers, may need a preliminary
split commit.

---

## Phase 4 — Platform-specific capability interfaces (~2 days) — ✅ complete

`IPlatformFactory` today declares `CreateTailscale`, `GetTailscalePostDeployAction`,
`GetTailscaleKeyManager`, `GetTenantKeycloakSeeder`, `CreateGateChecker`,
`CreateSeedBucket`, etc. — vendor-named capabilities that return null
when unsupported. That's the wrong axis. Replace with capability
interfaces core doesn't name.

### Approach

Move vendor-named capabilities off `IPlatformFactory` into a generic
capability lookup the caller obtains without a known-name method.

```csharp
// Lz.Core/Interfaces/IPlatformFactory.cs
public interface IPlatformFactory
{
    T? GetCapability<T>() where T : class;  // null if platform doesn't implement T

    // Shape-named methods stay: CreateNetwork, CreateDatabase, CreateFileStorage,
    // CreateComputeEnvironment, CreateService, CreateAuthService, CreateEmail,
    // CreateTenantCdn, CreateTenantData, CreateTenantService, CreateTransitionChecker,
    // LookupFoundation, DeployTenantDnsAndCert, CleanupBeforeFoundationAsync,
    // GetFoundationPostDeployAction, GetFoundationServiceDeployAction,
    // GetServiceDeployAction, UpdateTenantSplitDnsAsync (rename — see below),
    // GetConfigInitRunner, GetPostSeedRunner, GetAdminSetupRunner.
}

// Lz.Aws/Interfaces/ — AWS-named capability contracts
public interface IVpnSubnetRouter { ... }         // was ITailscaleComponent
public interface IVpnKeyManager { ... }            // was ITailscaleKeyManager
public interface ITenantRealmSeeder { ... }        // was ITenantKeycloakSeeder
public interface IVpcGateChecker { ... }           // was IGateCheckerComponent
```

### What disappeared from `Lz.Core/Interfaces/` (now in `Lz.Aws/Interfaces/`)

`ITailscaleComponent`, `ITailscaleKeyManager`, `ITenantKeycloakSeeder`,
`IGateCheckerComponent`, `IThemeDeployRunner`, plus `Outputs/ITailscaleOutputs`
and `Outputs/IGateCheckerOutputs`.

### Actual approach landed — `IAwsPlatformFactory` extension interface

The plan's original `GetCapability<T>()` service-locator pattern was
dropped in favour of a cleaner extension-interface approach:

```csharp
// Lz.Aws/Interfaces/IAwsPlatformFactory.cs
public interface IAwsPlatformFactory : IPlatformFactory
{
    ITailscaleComponent? CreateTailscale();
    Task UpdateTenantSplitDnsAsync(TenantConfig tenantConfig);
    IPostDeployAction? GetTailscalePostDeployAction(SystemDefinition? system = null);
    ITailscaleKeyManager? GetTailscaleKeyManager();
    ITenantKeycloakSeeder? GetTenantKeycloakSeeder();
    IGateCheckerComponent? CreateGateChecker();
    string? CreateSeedBucket(SharedConfig sharedConfig, string systemKey);
}
```

AWS factories (`AwsEcsPlatformFactory`, `AwsAppRunnerPlatformFactory`,
`AwsEcsExpressPlatformFactory`) implement `IAwsPlatformFactory`. AWS
orchestration code casts `_factory as IAwsPlatformFactory` on a private
`AwsFactory` helper and calls `AwsFactory?.Method()`. Azure stub
implements only `IPlatformFactory` and therefore doesn't carry the AWS
method signatures.

No interface-method renames landed. Tailscale/Keycloak/GateChecker names
accurately describe the AWS implementations and read more clearly than
generic synonyms like `IVpnSubnetRouter`/`ITenantRealmSeeder`.
`CreateSeedBucket` kept its name. If Azure ships its own VPN solution,
the right move there is to introduce `IAzurePlatformFactory` with
Azure-named methods, not to force a synonym across both.

### Orchestration

Already in `Lz.Aws/Orchestration/` from Phase 3 (Path C), so the
capability moves didn't require orchestration namespace changes. Only
change: `_factory.X()` → `AwsFactory?.X()` at each AWS-method call
site. Nullable-Task `await` pattern needed at one call site.

### BCProjNew update

No changes — BCProjNew's plugin doesn't call `IAwsPlatformFactory`
methods directly. Verified by clean rebuild against the new packages.

### Tests

Existing 44 tests continued to pass. No new capability-presence tests
added because the extension-interface pattern makes them compile-time
checks rather than runtime lookups.

---

## Phase 5 — Generic orchestration (deferred, not in this plan)

Not scheduled. The original deferral stands: defer until a second real
platform (Azure) is a committed target. What "Phase 5" means post-Path-C
is narrower than before — carving generic pre/post sequencing,
stack-creation, and gate dispatch back out of `SharedDeployment`/
`SystemDeployment` so both AWS and Azure orchestrators can share them.
That refactor is still best done with a real second consumer to design
against, not speculatively.

---

## Cross-cutting

### Testing rhythm

Every phase:
1. Land `Lz.*` change with unit tests green.
2. Update BCProjNew/Deploy in parallel.
3. Run `lz` commands against BCProjNew end-to-end (`lz deployassets
   --all`, `lz updatekvs`, `lz deploysystem --dry-run` if available).
4. Only then merge and move to the next phase.

### Commit cadence summary

- Phase 0: 1 commit (+1 test commit in `Lz.*`). No BCProjNew commit.
- Phase 1: 1 commit in `Lz.*`, 1 commit in BCProjNew.
- Phase 2: 1 commit in `Lz.*`, possibly 1 commit in BCProjNew (config
  additions only).
- Phase 3: 3 commits in `Lz.*` (one per derived type), 1–2 commits in
  BCProjNew (plugin cast updates + csproj ref).
- Phase 4: 4–5 commits in `Lz.*`, 0 commits in BCProjNew.

Total: ~10–12 `Lz.*` commits, ~3 BCProjNew commits. ~1.5 weeks of
focused work.

### Branch strategy

Stay on `IsolateTargets`. Merge to `master` at phase boundaries — each
phase is independently shippable. If any phase stalls, everything
before it is still a net improvement.

### Migration signal to users

Zero YAML key changes across all phases. `ConfigValidator` gains
one-shot warnings only if a user's YAML references a key that's
moved namespace — but `WithTypeMapping` preserves keys, so there should
be no cases. If Phase 3 surfaces one, we add an explicit validator
message naming the old key.

---

## Risks and open questions

1. **`CdnConfig` audit.** Not in the Tier 1 list but worth re-reading —
   if it names CloudFront concepts (KVS, origin access control), it
   belongs in `AwsSystemConfig` / `AwsTenantConfig` like `EcsConfig`.
   Decide during Phase 3, before writing `AwsSystemConfig`.
2. **`ConfigMerger` extensibility.** The `protected virtual MergeInto`
   pattern needs a proof-of-concept before committing to Phase 3 — if
   merge logic is tangled with runtime Secrets Manager helpers it may
   need to be split first.
3. **`SystemDefinition.UseAuth(realms[])` signature.** Today callers
   pass `["primary"]` for Cognito pools vs. `["tenantRealm"]` for
   Keycloak. Is the semantic interchangeable? BCProjNew only calls
   `UseCognito`, so the rename is safe for the test subject. Confirm
   before deleting `UseKeycloak` outright.
4. **Phase 4 capability model.** `T? GetCapability<T>()` vs.
   service-locator concerns — if we already have DI in the pipeline,
   prefer injecting the capability. Quick check of `Lz.Cli` wiring
   before committing.
5. **Plugin author friction.** `((AwsSystemConfig)config).AppRunner` is
   uglier than `config.AppRunner`. Mitigated by the `config.Aws()`
   extension helper pattern used inside `Lz.Aws` — if plugin authors
   feel this is friction, we can expose the same extension publicly
   from `Lz.Aws`.

## Related documents

- [`TargetIsolation.md`](TargetIsolation.md) — design + motivation + audit.
- `Platform/CognitoHardeningPlan.md` (in `BCProjNew`) — first concrete
  consumer of the derived-class pattern, validated by Phase 2.
