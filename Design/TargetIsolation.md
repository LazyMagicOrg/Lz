# Target Isolation — keeping platform concepts out of `Lz.Core`

## Motivation

`Lz.Core` is the platform-agnostic layer. It defines the contracts
(`IPlatformFactory`, output interfaces, config base types) that platform
libraries like `Lz.Aws` and `Lz.Azure` implement. The invariant we want:

> `Lz.Core` should know nothing about AWS, Azure, GCP, or any other
> specific cloud target. It speaks in shapes, not vendors.

This came into focus while reviewing `Platform/CognitoHardeningPlan.md`
in the `BCProjNew` repo: the plan proposed adding a `CognitoGroup`
class and `AdvancedSecurityMode` / `Groups` fields onto
`Lz.Core.Config.AuthConfigEntry`. Cognito is a Cognito-specific product,
so putting it in `Lz.Core` directly would have dragged AWS vocabulary
into the platform-neutral layer that every other provider has to
reference. That prompted a broader discussion, captured here.

## Options we considered

### Option A — Generalize the vocabulary, add an `AwsCognitoAuthExtras` sub-object

Rename the generic fields (`MfaConfiguration`, `PasswordMinLength`,
`Groups` → `AuthGroup`) and stash the AWS-only extras (`AdvancedSecurityMode`,
`RoleArn`) inside a nested `AwsCognito? AwsCognito { get; set; }` bag
that still lives in `Lz.Core`.

**Rejected.** Puts AWS-named types back in `Lz.Core`. "One small
escape hatch" quickly becomes the path of least resistance for every
future platform-specific field. The slippery-slope concern is real
and already visible in the existing code (`EcsConfig`, `AppRunnerConfig`
are examples of exactly this pattern that we now regret).

### Option B (composition) — `AwsAuthConfigEntry` with an inner `AuthConfigEntry`

`Lz.Aws` defines a wrapper that holds a core entry plus AWS extras.
Platform-specific code reads the wrapper; generic code reads the inner.

**Rejected.** "Has-a" is the wrong relationship. An AWS auth config
really is-a generic auth config with extras.

### Option C — Bag of attributes

`AuthConfigEntry` gains a `Dictionary<string, object> PlatformExtensions`;
AWS code reads `PlatformExtensions["cognito:advancedSecurityMode"]`.

**Rejected.** Stringly-typed. No compile-time validation. Config errors
surface late. Industry consensus is to avoid this unless there's no
alternative.

### Option D (chosen) — Derived class in `Lz.Aws` + YAML type mapping

`Lz.Core.Config.AuthConfigEntry` stays exactly as it is.
`Lz.Aws.Config.AwsAuthConfigEntry : AuthConfigEntry` adds the
Cognito-specific fields. The YAML deserializer is told, at startup,
"when you're asked for an `AuthConfigEntry`, build an
`AwsAuthConfigEntry` instead." Same YAML the user already writes.
Consumers in the AWS path cast via `is` when they need the extras.

**Chosen.** Inheritance exactly models the relationship. `Lz.Core`
learns zero AWS vocabulary. `Lz.Aws` owns its extensions completely.
The pattern scales to every other platform-specific extension we
haven't yet made.

## The mechanism — `IConfigExtensions`

### Contract

```csharp
// Lz.Core/Config/IConfigExtensions.cs
public interface IConfigExtensions
{
    void Configure(DeserializerBuilder builder);
}
```

### Loader integration

`Lz.Core.Config.ConfigLoader` accumulates registered extensions in a
static list and lazily rebuilds its PascalCase deserializer when the
list changes:

```csharp
public static void RegisterExtensions(IConfigExtensions extensions);
```

Each registered extension's `Configure(DeserializerBuilder)` runs while
the deserializer is being built. Typical call:

```csharp
// in Lz.Aws.Config.AwsConfigExtensions (once a derived type exists)
public void Configure(DeserializerBuilder builder)
{
    builder.WithTypeMapping<AuthConfigEntry, AwsAuthConfigEntry>();
}
```

### Startup

`Lz.Cli/Program.Main` registers the AWS extensions before any config
is loaded:

```csharp
ConfigLoader.RegisterExtensions(new AwsConfigExtensions());
```

Azure (when it ships) will register its own `AzureConfigExtensions`
the same way.

### Per-invocation flow

1. `ConfigLoader.LoadSystemConfig(...)` is called.
2. The lazy deserializer is built — every registered extension runs its
   `Configure`, each adding its type mappings.
3. YAML is deserialized. `Dictionary<string, AuthConfigEntry>` entries
   materialise as `AwsAuthConfigEntry` (because AWS registered the
   mapping) even though the declared property type in `SystemConfig`
   is still `AuthConfigEntry`.
4. Downstream AWS components cast: `if (entry is AwsAuthConfigEntry aws)`.

### Current state

- **Plumbing landed**, empty AWS implementation, no type mappings
  registered yet. The next change to touch AWS-specific auth fields
  (the Cognito hardening work) is what exercises it.

### Known caveat

`WithTypeMapping<T, U>()` is unconditional when called. Today that's
fine because AWS is the only platform. When Azure lands, the
registration must become platform-aware: peek `SystemConfig.Platform`
from a preliminary deserialise (or from the filename + a lightweight
scan), then register only the matching extension. Flagged in
`Platform/CognitoHardeningPlan.md` §1e.

## Audit — where AWS already leaks into `Lz.Core`

The `CognitoGroup` concern was the visible tip of a much larger
pattern. This audit records the existing state so we can reason about
it as a cleanup backlog, not a hidden debt.

### Tier 1 — Structural leakage (AWS-named types or members `Lz.Core` owns)

**Config types built around AWS concepts.**

| File | What leaks |
|---|---|
| `Lz.Core/Config/SystemConfig.cs` | `EcsConfig? ECS`, `AppRunnerConfig? AppRunner`, `CdnConfig? CDN`, `CentralAuthDomain`, `SharedSecretArn`, `SharedKmsKeyArn`, `SharedProfile`, `SharedRegion`, `TrustedAccountIds` |
| `Lz.Core/Config/TenantConfig.cs` | same AWS subsections, `AcmCertificateArn`, `HostedZoneId`, `SharedSecretArn/KmsKeyArn`, `CentralAuthDomain`, comments referring to "CloudFront function JS files" / "EFS smartstore-config" / "SSM Parameter Store" |
| `Lz.Core/Config/SharedConfig.cs` | `SharedKeycloakConfig Keycloak`, `TailscaleInstanceType`, `TailscaleDesiredCapacity`, S3-bucket `SharedSuffix` |
| `Lz.Core/Config/EcsConfig.cs` | entire class — `KeycloakImageTag/Cpu/Memory/ThemePath`, `TailscaleInstanceType/DesiredCapacity` |
| `Lz.Core/Config/AppRunnerConfig.cs` | entire class is AWS App Runner |
| `Lz.Core/Config/StateConfig.cs` | Pulumi-specific (S3/KMS URL schemes) |
| `Lz.Core/Config/SeedDataConfig.cs` | S3-bucket shaped |
| `Lz.Core/Config/KeycloakSeedConfig.cs` | entire class — a specific auth tool |
| `Lz.Core/Config/ConfigValidator.cs` | platform-aware validation messages referencing Cognito/Keycloak/topologies by name |
| `Lz.Core/Config/ConfigMerger.cs` | `GetEffectiveEcs`, `SecretsManagerConfig` helpers |

**Definitions.**

| File | What leaks |
|---|---|
| `Lz.Core/Definitions/SystemDefinition.cs` | `UseCognito(string[] pools)`, `Provider = "cognito"` string, `UseKeycloak`, `UseTailscale` |

**Platform factory and component contracts.**

| File | What leaks |
|---|---|
| `Lz.Core/Interfaces/IPlatformFactory.cs` | method names + doc comments reference Tailscale, Keycloak, Lambda, ECS, ECR, IAM, S3, VPC, Route53, SES. Methods themselves (`CreateTailscale`, `GetTailscalePostDeployAction`, `GetTenantKeycloakSeeder`, `CreateGateChecker`) are vendor-named. |
| `Lz.Core/Interfaces/ITailscaleComponent.cs`, `ITailscaleKeyManager.cs`, `Outputs/ITailscaleOutputs.cs` | whole files Tailscale-named |
| `Lz.Core/Interfaces/ITenantKeycloakSeeder.cs`, `IThemeDeployRunner.cs` | Keycloak-named |
| `Lz.Core/Interfaces/IGateCheckerComponent.cs`, `Outputs/IGateCheckerOutputs.cs` | described as "Lambda-based gate checker" |
| `Lz.Core/Interfaces/ISeedTaskComponent.cs`, `Outputs/ISeedTaskOutputs.cs` | `EcrRepositoryUrl { get; }` property — literal ECR |
| `Lz.Core/Interfaces/Outputs/IAuthPoolOutputs.cs` | doc comment "e.g., Cognito user pool ID, client ID, metadata URL"; "enabling downstream components (e.g., CloudFront KVS) to auto-wire" |

### Tier 2 — Orchestration references AWS in logic and comments

| File | Hits | Notes |
|---|---|---|
| `Lz.Core/Orchestration/SharedDeployment.cs` | 44 | coordinates Tailscale/Keycloak flows by name |
| `Lz.Core/Orchestration/SystemDeployment.cs` | 43 | "ECS/Keycloak is deployed via SharedDeployment; Cognito is per-environment"-style branches |
| `Lz.Core/Orchestration/TransitionRequirement.cs` | 5 | — |
| `Lz.Core/Orchestration/StackOutputReader.cs` | 1 | comment |

These don't import the AWS SDK; they branch on topology strings and
call through `IPlatformFactory`. The leakage is mostly in naming and
in the shape of `SystemDeployment` (ordered around ECS+Keycloak-specific
phases — shared-account Keycloak seeding, EFS data gates, ECR image
push).

### Tier 3 — Pulumi leakage (separate axis)

**Every** output interface under `Lz.Core/Interfaces/Outputs/*.cs`
imports `using Pulumi;` and exposes `Pulumi.Output<T>` properties.
Not AWS-specific, but it's the tightest coupling in the codebase —
swapping Pulumi for Terraform CDK or CloudFormation would require
changing every output interface.

Files affected: `IAuthPoolOutputs`, `ICdnOutputs`, `IComputeEnvironmentOutputs`,
`IDatabaseOutputs`, `IEmailOutputs`, `IFileStorageOutputs`,
`IGateCheckerOutputs`, `INetworkOutputs`, `ISeedTaskOutputs`,
`IServiceOutputs`, `ITailscaleOutputs`, `ITenantDataOutputs`.

### Tier 4 — Non-leakage that looks like leakage

- `Profile`, `Region`, `SystemDomain` on `SystemConfig` — generic
  cloud-deployment concepts.
- `RootDomain`, `HostedZoneId` on `TenantConfig` — DNS concepts
  (`HostedZoneId` is Route 53-shaped; Azure DNS zones aren't called
  that, so this is borderline — acceptable for now).
- **Plumbing landed and exercised.** `AwsConfigExtensions` registers
  type mappings for `AuthConfigEntry`, `SystemConfig`, `TenantConfig`,
  and `SharedConfig`. Round-trip tests in `Lz.Tests/Config.Tests/`
  verify the derived types materialise under the AWS platform.
- **Derived config types live in `Lz.Aws/Config/`:** `AwsSystemConfig`,
  `AwsTenantConfig`, `AwsSharedConfig`, `AwsAuthConfigEntry`. AWS-specific
  fields (`ECS`, `AppRunner`, `SharedSecretArn/KmsKeyArn/Profile/Region`,
  `TrustedAccountIds`, `AcmCertificateArn`, `HostedZoneId`, `Keycloak`,
  `TailscaleInstanceType/DesiredCapacity`, Cognito hardening fields) are
  carried there.
- **`AwsConfigCast` helper** exposes `.Aws()` extension methods for
  plugin authors and `Lz.Aws` internals to cast the base config to its
  derived type.

### Known caveat — resolved

The original concern — "`WithTypeMapping<T, U>()` is unconditional;
multiple platforms' registrations will collide" — is resolved.
`ConfigLoader.BuildDeserializer` filters extensions by
`ConfigLoader.ActivePlatform` (defaults to `"aws"`, switched by
scanning each loaded YAML for a top-level `Platform:` key). Only the
matching extension contributes mappings.

## Audit — resolution status

The audit below was the original snapshot of AWS leakage into `Lz.Core`.
Tiers 1 and 2 are now resolved via Phases 0–4 of `Design/TargetIsolationPlan.md`.
Tier 3 (Pulumi coupling) is out of scope per explicit decision. Tier 4
remains acceptable.

### Tier 1 — Structural leakage — ✅ resolved

**Config types built around AWS concepts.**

| File | Status |
|---|---|
| `Lz.Core/Config/SystemConfig.cs` | AWS fields moved to `Lz.Aws/Config/AwsSystemConfig.cs`. Base keeps only platform-neutral fields. |
| `Lz.Core/Config/TenantConfig.cs` | AWS fields moved to `Lz.Aws/Config/AwsTenantConfig.cs`. |
| `Lz.Core/Config/SharedConfig.cs` | AWS fields + `SharedKeycloakConfig` moved to `Lz.Aws/Config/AwsSharedConfig.cs`. |
| `Lz.Core/Config/EcsConfig.cs` | Moved to `Lz.Aws/Config/EcsConfig.cs`. |
| `Lz.Core/Config/AppRunnerConfig.cs` | Moved to `Lz.Aws/Config/AppRunnerConfig.cs`. |
| `Lz.Core/Config/KeycloakSeedConfig.cs` | Moved to `Lz.Aws/Config/KeycloakSeedConfig.cs`. |
| `Lz.Core/Config/BootstrapCredsConfig.cs` | Moved to `Lz.Aws/Config/BootstrapCredsConfig.cs`. |
| `Lz.Core/Config/StateConfig.cs` | Stays. `Backend` + `SecretsProvider` are generic URL strings; not moved because Pulumi-independence is not a goal. |
| `Lz.Core/Config/SeedDataConfig.cs` | Stays. `Bucket` + `Region` are generic shapes. |
| `Lz.Core/Config/ConfigValidator.cs` | CentralAuthDomain requirement dropped; topology-specific validation stays topology-string-based and generic. |
| `Lz.Core/Config/ConfigMerger.cs` | `GetEffectiveEcsConfig` moved to `Lz.Aws/Config/AwsConfigMerger.cs`. Generic merges stay. |

**Definitions.**

| File | Status |
|---|---|
| `Lz.Core/Definitions/SystemDefinition.cs` | `UseCognito`/`UseKeycloak`/`UseTailscale` replaced by `UseAuth(realms[])` + `UseVpn()`. Base class no longer sets `Provider`. |

**Platform factory and component contracts.**

| File | Status |
|---|---|
| `Lz.Core/Interfaces/IPlatformFactory.cs` | Vendor-named methods moved to `Lz.Aws/Interfaces/IAwsPlatformFactory.cs`. Core factory keeps shape-named methods only. All doc comments scrubbed of vendor names. |
| `Lz.Core/Interfaces/ITailscaleComponent.cs`, `ITailscaleKeyManager.cs`, `Outputs/ITailscaleOutputs.cs` | Moved to `Lz.Aws/Interfaces/`. |
| `Lz.Core/Interfaces/ITenantKeycloakSeeder.cs`, `IThemeDeployRunner.cs` | Moved to `Lz.Aws/Interfaces/`. |
| `Lz.Core/Interfaces/IGateCheckerComponent.cs`, `Outputs/IGateCheckerOutputs.cs` | Moved to `Lz.Aws/Interfaces/`. |
| `Lz.Core/Interfaces/ISeedTaskComponent.cs`, `Outputs/ISeedTaskOutputs.cs` | Stay in core. `EcrRepositoryUrl` renamed to `ContainerImageRepositoryUrl`. |
| `Lz.Core/Interfaces/Outputs/IAuthPoolOutputs.cs` | Doc comments scrubbed. |

### Tier 2 — Orchestration references AWS — ✅ resolved

`SharedDeployment.cs` and `SystemDeployment.cs` were wholesale
AWS-ECS-Keycloak-Tailscale shaped. Both moved to
`Lz.Aws/Orchestration/` in Phase 3 (see `TargetIsolationPlan.md`
Path C). Core keeps the generic orchestration scaffolding:
`PulumiPathResolver`, `StackOutputReader`, `TransitionGate`,
`TransitionRequirement`.

AWS orchestration holds a platform-neutral `IPlatformFactory` reference
and casts to `IAwsPlatformFactory` only where it needs AWS-named
capabilities, via a private `AwsFactory => _factory as IAwsPlatformFactory`
helper.

When a second platform lands, the generic-orchestrator refactor
becomes worth doing — until then, moving the orchestrators wholesale
to `Lz.Aws` keeps `Lz.Core` honest without the speculative abstraction
the design doc originally warned against.

### Tier 3 — Pulumi coupling — out of scope

Output interfaces continue to import `using Pulumi;` and expose
`Pulumi.Output<T>`. Swapping Pulumi for another IaC engine is
explicitly not a goal; re-evaluate only if that changes.

### Tier 4 — Non-leakage that looks like leakage — unchanged

- `Profile`, `Region`, `SystemDomain` on `SystemConfig` — generic
  cloud-deployment concepts.
- `RootDomain` on `TenantConfig` — generic DNS concept.
- `HostedZoneId` / `AcmCertificateArn` moved to `AwsTenantConfig`
  (they're AWS-shaped enough to belong there).

## Forward-looking policy

The guiding rule going forward:

> **No new AWS / Tailscale / Keycloak / Cognito / Pulumi-named types
> in `Lz.Core`.** If a new concept is genuinely platform-specific,
> derive it under `Lz.Aws/Config/` (or the equivalent in a future
> `Lz.Azure/Config/`) and register its YAML type mapping via
> `IConfigExtensions`. Consumers in that platform cast from the base
> type to the derived type when they need the extras.

When we touch an existing AWS-named type in `Lz.Core`, prefer
> **No cloud-deployment mechanics in `Lz.Core`.** That means no AWS /
> Azure / GCP SDK references, no ARNs or resource-provider URIs, no
> profile/region resolution, no IaC-engine types (Pulumi, Terraform
> CDK), and no cloud-specific config fields on `Lz.Core` types. If a
> new concept is platform-specific, derive it under the platform
> library (`Lz.Aws/`, future `Lz.Azure/`) and — for config — register
> its YAML type mapping via `IConfigExtensions`.

> **Cloud-neutral vendor machinery is allowed in `Lz.Core`.** A plain
> HTTP client for an off-the-shelf product (Tailscale API client,
> Keycloak admin client, the seed-config models they consume) can live
> in `Lz.Core` because the product itself is cloud-neutral: the same
> client code works whether the server runs on AWS ECS, Azure Container
> Apps, or bare metal. What *stays* in the platform library is the
> *cloud-specific orchestration* around that product — e.g.
> `AwsTailscalePostDeployAction` (EC2 ASG recycling + Secrets Manager)
> and `AwsTenantKeycloakSeeder` (admin creds from AWS Secrets Manager).

When we touch an existing AWS-specific type in `Lz.Core`, prefer
**migrating it** to the platform library via a derived type and a
type mapping over leaving it in place. Cleanup is opportunistic rather
than scheduled.

## Cleanup backlog (only if ever prioritised)

The audit above doubles as a backlog, roughly three passes:

1. **Pass 1 — names and docs.** Rename `UseCognito` → `UseAuth`, scrub
   AWS product names from `IPlatformFactory` / `Outputs/*` comments,
   rename `ECS:` / `AppRunner:` YAML sections to `Compute:` /
   `Network:` / `Storage:` with per-provider sub-types. Low risk,
   cosmetic.

2. **Pass 2 — typed config sections.** Move `EcsConfig`,
   `AppRunnerConfig`, `StateConfig`, `SeedDataConfig`, `SharedKeycloakConfig`,
   `TailscaleInstanceType/DesiredCapacity`, `SharedSecretArn/KmsKeyArn`,
   `AcmCertificateArn`, `TrustedAccountIds`, etc. out of `Lz.Core`
   and into `Lz.Aws` as derived config types (`AwsSystemConfig`,
   `AwsTenantConfig`, `AwsSharedConfig`). Medium-sized refactor; uses
   the `IConfigExtensions` mechanism.

3. **Pass 3 — orchestration abstraction.** Replace topology-aware
   branching in `SystemDeployment` with platform-owned flow objects.
   Large refactor; worthwhile only when a second real platform (Azure)
   is a committed target.

No scheduled pass. Prioritise only when the structural cost becomes
visible — e.g., adding Azure, or a second AWS topology that doesn't
fit the current shape.
### Where Keycloak / Tailscale machinery lives

| Kind | Location | Example |
|---|---|---|
| Vendor API client / data models | `Lz.Core/{Vendor}/` | `KeycloakAdminClient`, `KeycloakSeeder`, `KeycloakSeedConfig`, `TailscaleApiClient` |
| Cloud-specific orchestration using the vendor | `Lz.Aws/` or future `Lz.Azure/` | `AwsTenantKeycloakSeeder` (reads creds from Secrets Manager), `AwsTailscalePostDeployAction` (recycles EC2 ASG instances) |
| Capability interfaces connecting orchestration to a platform factory | `Lz.Aws/Interfaces/` on `IAwsPlatformFactory` | `ITenantKeycloakSeeder`, `ITailscaleComponent`, `ITailscaleKeyManager` |

The capability interfaces stay in the platform library because the
factory method that exposes them (`GetTenantKeycloakSeeder`,
`CreateTailscale`) only makes sense for platforms that actually
deploy Keycloak / Tailscale. When a second platform ships its own
deploy path for the same product, promote the interface to a
cloud-neutral home at that point.

## Cleanup backlog — historical

The original backlog described three passes. Passes 1 and 2 landed in
Phases 0–4 of `Design/TargetIsolationPlan.md`. Pass 3 (generic
orchestration) folded into Phase 3 as "Path C" by moving the AWS-shaped
orchestrators wholesale to `Lz.Aws/Orchestration/`, rather than
factoring out platform-neutral flow objects speculatively.

The forward-looking follow-up, when a second real platform is
committed, is to carve generic orchestration back out — shared pre/post
sequencing, stack-creation, gate dispatch — so AWS and Azure
orchestrators both subclass or compose it. Not scheduled.

## Related documents

- `Platform/CognitoHardeningPlan.md` (in the `BCProjNew` repo) — first
  concrete use of the derived-class pattern, revised in response to
  this discussion.
