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

## Forward-looking policy

The guiding rule going forward:

> **No new AWS / Tailscale / Keycloak / Cognito / Pulumi-named types
> in `Lz.Core`.** If a new concept is genuinely platform-specific,
> derive it under `Lz.Aws/Config/` (or the equivalent in a future
> `Lz.Azure/Config/`) and register its YAML type mapping via
> `IConfigExtensions`. Consumers in that platform cast from the base
> type to the derived type when they need the extras.

When we touch an existing AWS-named type in `Lz.Core`, prefer
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

## Related documents

- `Platform/CognitoHardeningPlan.md` (in the `BCProjNew` repo) — first
  concrete use of the derived-class pattern, revised in response to
  this discussion.
