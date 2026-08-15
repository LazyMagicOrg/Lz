# Lz 0.11.0 — the axis restructure (+ AppRunner retirement)

> **Status: EXECUTED 2026-08-14 (pending downstream gates).** The sweep has run against
> the true pre-state (`origin/main` + split DNS, commit `3d29b0e`): the working tree was at
> the ORIGINAL `EcsExpress/` + `AwsEcsExpress*` names — the unpublished 0.10.48 intermediate
> rename (`EcsExpressRename.md`, `EcsFargateCognitoDynamodb/` folder) was reset away and
> never shipped; this doc's Old column reflects the real pre-state. Lz gates passed
> (build 0 errors; 252/252 tests — one apprunner-factory test legitimately deleted from the
> 253 baseline). Remaining gates: Scutara Deploy zero-CS0618 rebuild, `lz previewsystem` +
> `lz previewtenant` "no changes", then the per-system migration order below.
> This remains the authoritative old→new mapping the per-system `LzMigration.md` files
> defer to.

## The grammar (two rules carry everything)

1. **Components are named by capability, never by topology.** `Lz.Aws` gains axis folders —
   `Compute/`, `Auth/`, `Data/`, `Storage/`, `Edge/`, `Ops/` — and the four topology folders
   (`AppRunner/`, `Ecs/`, `EcsExpress/`, `Lambda/`) dissolve into them.
   (`Lz.Aws/Lambda/` survives as an **assets-only** folder — `gate-checker.zip` + its build
   script — because the runtime probing path `Lambda/gate-checker.zip` next to the loaded
   `Lz.Aws.dll` is a frozen output-layout identity; no `.cs` lives there.)
2. **Topology names appear only in `Topologies/`** — which becomes the assembly locus: the
   registry AND the factories, foundation lookups, and topology-bound post-deploy actions.
   Exempt: the frozen Pulumi type tokens (deployed-state URN identities, never renamed),
   the `[Obsolete]` shim namespaces (`Lz.Aws/Shims/`), and one AWS-owned string (below).

Mechanically checkable invariant: outside `Topologies/`, `Shims/`, and frozen-token literals,
`grep -E 'EcsExpress|AppRunner'` over `Lz.Aws/**/*.cs` is empty — **verified at execution**,
with exactly one justified remaining hit:
`Compute/Fargate/AwsFargateServiceComponent.cs` attaches the AWS-managed policy
`arn:aws:iam::aws:policy/service-role/AWSAppRunnerServicePolicyForECRAccess` — that is
AWS's own policy name and a deployed-role identity, frozen with an in-code comment.

**Family tags** (needed where the two Fargate lineages own same-shaped components):
- *(unqualified)* — the modern CloudFront-KVS-entry, Cognito+DynamoDB lineage (ex-`EcsExpress`).
- **`FargateAlb`** — the dual-ALB-entry platform lineage (ex-`Ecs`, the keycloak family).

Existing capability folders (`Config/`, `Docker/`, `DynamoDB/`, `Secrets/`, `Shared/`,
`Tailscale/`, `VectorStore/`, `Webapp/`, `Verification/`, `Orchestration/`, `Interfaces/`)
already satisfy rule 1 and are **unchanged this wave** (optional later nesting is polish, not
grammar).

## Mapping — Topologies/ (moves in; topology names allowed here)

| Old (folder / type) | New |
|---|---|
| `EcsExpress/AwsEcsExpressPlatformFactory` | `Topologies/AwsEcsFargateCognitoDynamodbPlatformFactory` (renamed to its topology string) |
| `Ecs/AwsEcsPlatformFactory` | `Topologies/AwsEcsFargateKeycloakPlatformFactory` (renamed to its topology string) |
| `Lambda/AwsLambdaPlatformFactory` | `Topologies/AwsLambdaCognitoDynamodbPlatformFactory` |
| `EcsExpress/AwsEcsExpressFoundationPostDeployAction` | `Topologies/AwsEcsFargateCognitoDynamodbFoundationPostDeployAction` |
| `EcsExpress/AwsEcsExpressPostDeployAction` | `Topologies/AwsEcsFargateCognitoDynamodbPostDeployAction` |
| `EcsExpress/AwsEcsExpressFoundationLookup` (static) | `Topologies/AwsEcsFargateCognitoDynamodbFoundationLookup` (hard rename — see shim policy) |
| `Ecs/AwsFoundationPostDeployAction` | `Topologies/AwsEcsFargateKeycloakFoundationPostDeployAction` |
| `Ecs/AwsServicesPostDeployAction` | `Topologies/` (move, name keeps) |
| `Ecs/AwsFoundationLookup` (static; **NTS forks it**) | `Topologies/AwsEcsFargateKeycloakFoundationLookup` (hard rename) |
| `AppRunner/AwsAppRunnerFoundationLookup` (static) | `Topologies/AwsLambdaCognitoDynamodbFoundationLookup` (lambda-only after AppRunner dies; hard rename) |
| `Ecs/AwsEcsPostDeployHelper` (static) | `Topologies/` (move, name keeps; namespace change is a hard break) |
| `Lambda/AwsLambdaPostDeployAction` | `Topologies/` (move, name keeps — "Lambda" is a capability) |

## Mapping — Compute/

**`Compute/Fargate/`** (modern lineage, unqualified; namespace `Lz.Aws.Compute.Fargate`):

| Old | New | Frozen token |
|---|---|---|
| `AwsEcsExpressNetworkComponent` | `AwsFargateNetworkComponent` | `lz:aws:EcsExpressNetwork` |
| `AwsEcsExpressComputeComponent` | `AwsFargateComputeComponent` | `lz:aws:EcsExpressCompute` |
| `AwsEcsExpressTenantServiceComponent` | `AwsFargateTenantServiceComponent` | `lz:aws:EcsExpressTenantService` |
| `AwsEcsExpressNetworkOutputs` / `AwsEcsExpressComputeOutputs` | `AwsFargateNetworkOutputs` / `AwsFargateComputeOutputs` | — |
| *(internal)* `AwsEcsExpressServiceOutputs` | `AwsFargateTenantServiceOutputs` (internal; renamed at execution to avoid colliding with the ex-AppRunner `AwsFargateServiceOutputs` below) | — |
| **(addendum)** `AppRunner/AwsAppRunnerServiceComponent` | `Compute/Fargate/AwsFargateServiceComponent` — the `IServiceComponent` the modern Fargate factory uses; it reads no AppRunner config block (only IAM), so no config-read flip was needed | `lz:aws:AppRunnerService` |
| *(internal)* `AwsAppRunnerServiceOutputs` | `AwsFargateServiceOutputs` (internal) | — |

**`Compute/FargateAlb/`** (keycloak lineage; **NTS forks the network component**; namespace `Lz.Aws.Compute.FargateAlb`):

| Old | New | Frozen token |
|---|---|---|
| `Ecs/AwsEcsNetworkComponent` | `AwsFargateAlbNetworkComponent` | `lz:aws:EcsNetwork` |
| `Ecs/AwsEcsClusterComponent` | `AwsFargateAlbClusterComponent` | `lz:aws:EcsCluster` |
| `Ecs/AwsEcsTenantServiceComponent` | `AwsFargateAlbTenantServiceComponent` | `lz:aws:EcsTenantService` |
| *(internal)* `AwsEcsTenantServiceOutputs` | `AwsFargateAlbTenantServiceOutputs` (internal) | — |
| `Ecs/AwsEcsServiceComponent` | `AwsFargateAlbServiceComponent` | — |
| `Ecs/AwsNetworkOutputs` / `AwsComputeOutputs` / `AwsServiceOutputs` | `AwsFargateAlb{Network,Compute,Service}Outputs` (`AwsFargateAlbServiceOutputs` also moved out of the Keycloak component file into its own file) | — |
| `Ecs/AwsTransitionChecker` | `AwsFargateAlbTransitionChecker` | — |
| `Ecs/AwsTenantDnsAndCertComponent` | `Compute/FargateAlb/` (move, name keeps) | `lz:aws:TenantDnsAndCert` |

**`Compute/Lambda/`** ("Lambda" = capability; names keep unless noted; namespace `Lz.Aws.Compute.Lambda`):

| Old | New |
|---|---|
| `Lambda/AwsLambda{Compute,TenantService,Service}Component`, `AwsLambdaApiOriginHolder`, `AwsLambdaContainerUpdater`, `AwsLambda{ConfigInit,AdminSetup,PostSeed,ThemeDeploy}Runner`, `AwsLambdaInfra` | `Compute/Lambda/` (move only) |
| `AppRunner/AwsAppRunnerNetworkComponent` (serverless: certs/zones, no VPC) | `Compute/Lambda/AwsLambdaNetworkComponent` (token `lz:aws:AppRunnerNetwork` frozen) |
| `AppRunner/AwsAppRunnerNetworkOutputs` / `AwsAppRunnerComputeOutputs` | `AwsLambdaNetworkOutputs` / `AwsLambdaComputeOutputs` |

## Mapping — Auth/, Data/, Storage/, Edge/, Ops/, Shared/, Tailscale/

| Old | New | Frozen token |
|---|---|---|
| `AppRunner/AwsAppRunnerCognitoComponent` (+`CognitoCustomAuth/` assets, csproj paths updated; nupkg/output layout unchanged) | `Auth/AwsCognitoComponent` | `lz:aws:Cognito` (already axis-named!) |
| `CognitoPoolOutputs` | `Auth/` (move, name keeps) | — |
| **(addendum)** `AwsAppRunnerCognitoOutputs` | `Auth/AwsCognitoOutputs` (public outputs class in the same file; follows the outputs-rename pattern) | — |
| `Ecs/AwsKeycloakEcsComponent` | `Auth/AwsKeycloakServiceComponent` | `lz:aws:KeycloakEcs` |
| `Ecs/AwsTenantKeycloakSeeder`, `Ecs/SmartstoreCognitoWiring` | `Auth/` (move; names keep — Smartstore wiring flagged as product-specific leftover, out of scope) | — |
| `AppRunner/AwsAppRunnerDynamoDbComponent` | `Data/AwsDynamoDbComponent` | `lz:aws:DynamoDB` |
| `Ecs/AwsRdsComponent` | `Data/` (move, name keeps) | `lz:aws:Rds` |
| `AppRunner/AwsAppRunnerTenantDataComponent` | `Data/AwsTenantDataComponent` ⚠ name-swap | `lz:aws:AppRunnerTenantData` |
| `Ecs/AwsTenantDataComponent` | `Data/AwsFargateAlbTenantDataComponent` ⚠ frees the name above (renamed FIRST; its internal `AwsTenantDataOutputs` became `AwsFargateAlbTenantDataOutputs`, freeing that name too) | `lz:aws:TenantData` |
| `AppRunner/AwsAppRunnerDatabaseOutputs` / `AwsAppRunnerTenantDataOutputs` | `AwsDynamoDbOutputs` / `AwsTenantDataOutputs` | — |
| `AppRunner/AwsAppRunnerFileStorageComponent` (+ internal `AwsAppRunnerFileStorageOutputs` → `AwsS3FileStorageOutputs`) | `Storage/AwsS3FileStorageComponent` | — |
| `Ecs/AwsEfsComponent` | `Storage/` (move, name keeps) | `lz:aws:Efs` |
| `EcsExpress/AwsEcsExpressCloudFrontComponent` | `Edge/AwsCloudFrontKvsComponent` | `lz:aws:EcsExpressCloudFront` |
| `Lambda/AwsLambdaCloudFrontComponent` (derives the above) | `Edge/AwsCloudFrontKvsLambdaComponent` | — |
| `Ecs/AwsCloudFrontComponent` (static behaviors) | `Edge/AwsCloudFrontStaticComponent` | `lz:aws:CloudFront` |
| `Ecs/AwsEdgeUpdater` | `Edge/` (move, name keeps) | — |
| `Ecs/AwsSesComponent`, `AwsSeedTaskComponent`, `AwsSeedRunner`, `AwsParkManager`, `AwsContainerUpdater`, `AwsPrivateZoneCleanup`†, `AwsTenantConfigPublisher`† ; `Lambda/AwsGateCheckerLambdaComponent` | `Ops/` (move; names keep; † statics — hard namespace break, see shim policy) | `lz:aws:Ses`, `lz:aws:SeedTask`, `lz:aws:GateChecker` |
| `AppRunner/AwsAppRunnerTransitionChecker` | `Ops/AwsTransitionChecker` (unqualified; the ex-Ecs one is `AwsFargateAlbTransitionChecker`, renamed FIRST) | — |
| `AppRunner/BffWiring`, `BffStackOutputs` (both internal) | `Shared/` (move, names keep) | — |
| **(addendum)** `Ecs/AwsTailscaleAsgComponent` | `Tailscale/` (the EXISTING capability folder; namespace `Lz.Aws.Tailscale`; name keeps) | `lz:aws:TailscaleAsg` |

## Deleted — the `apprunner` topology (no shims; compile error if referenced — fleet audit: nobody does)

`AwsAppRunnerPlatformFactory`, `AwsAppRunnerComputeComponent`, `AwsAppRunnerCloudFrontComponent`,
`AwsAppRunnerTenantServiceComponent`, `AwsAppRunnerPostDeployAction`; `AwsTopologies.AppRunner`
field + registration; `AwsComputeKind.AppRunner` enum member; **`AppRunnerConfig` + the
`AwsSystemConfig.AppRunner` and `AwsTenantConfig.AppRunner` properties, and the
`AwsConfigMerger` legacy `AppRunner:`→`Fargate` fallback** — every SystemDefinition in the
fleet (all four descend from one template) replaces `config.Aws().AppRunner?.Port/HealthCheckPath`
with `config.Aws().Fargate?.…` (identical behavior today — the AppRunner reads already fell
through to the same defaults). Their tokens (`lz:aws:AppRunnerCloudFront`,
`lz:aws:AppRunnerCompute`) left the codebase with the files; the SystemDeployment
topology-detection reads still recognize legacy `apprunner` deployed state byte-identically.
**Precondition:** a live sanity check that no account still has an `apprunner`-topology stack.

## Shim policy

One file per retired namespace, under `Lz.Aws/Shims/`
(`EcsExpressNamespaceShims.cs`, `EcsNamespaceShims.cs`, `AppRunnerNamespaceShims.cs`,
`LambdaNamespaceShims.cs`). Every renamed/moved public **non-sealed, non-static instance**
type (with an accessible constructor) keeps an `[Obsolete]` empty derived class in its old
namespace, chaining the constructor (warnings, not breaks). Message format:
`Renamed to {new fully-qualified name} (Lz 0.11.0). See Lz/Migrations/AxisRestructure.md;
this shim will be removed in a future release.`

**Statics are documented hard renames, not shims** — fleet audits show no external consumer
calls Lz statics, so no forwarding wrappers ship. The hard-but-mechanical breaks:

| Break | Fix |
|---|---|
| static `AwsEcsExpressFoundationLookup` | → `Lz.Aws.Topologies.AwsEcsFargateCognitoDynamodbFoundationLookup` |
| static `AwsFoundationLookup` | → `Lz.Aws.Topologies.AwsEcsFargateKeycloakFoundationLookup` |
| static `AwsAppRunnerFoundationLookup` | → `Lz.Aws.Topologies.AwsLambdaCognitoDynamodbFoundationLookup` |
| static `AwsEcsPostDeployHelper` | → `Lz.Aws.Topologies.AwsEcsPostDeployHelper` (namespace only) |
| static `AwsPrivateZoneCleanup`, `AwsTenantConfigPublisher` | → `Lz.Aws.Ops.*` (namespace only) |
| static `SmartstoreCognitoWiring` | → `Lz.Aws.Auth.SmartstoreCognitoWiring` (namespace only) |
| sealed `AwsLambdaApiOriginHolder` | → `Lz.Aws.Compute.Lambda.AwsLambdaApiOriginHolder` (namespace only) |
| enums `UpdateOutcome`, `EdgeUpdateOutcome` | → `Lz.Aws.Ops.UpdateOutcome`, `Lz.Aws.Edge.EdgeUpdateOutcome` (enums cannot be derived) |
| internal `BffWiring` / `BffStackOutputs` | → `Lz.Aws.Shared.*` (internal — no external surface) |

Records (`ContainerUpdateResult`, `EdgeFunctionResult`) ARE shimmed via derived records.
Deleted AppRunner types get nothing. Shims are removed in a later release; warnings are the
migration deadline.

## Judgment calls locked at approval (flag disagreement before the sweep)

1. **`FargateAlb`** as the keycloak-lineage family tag (alternatives considered: `Platform`,
   `Classic` — rejected as vaguer).
2. **Factories renamed to their full topology strings** (`AwsEcsFargateKeycloakPlatformFactory`,
   `AwsLambdaCognitoDynamodbPlatformFactory`) so factory ⇄ config string is eyeball-matchable.
3. **Unqualified names go to the modern lineage**, including the `AwsTenantDataComponent`
   name swap (sweep renamed the Ecs one first to avoid a transient collision).
4. Ex-AppRunner serverless network becomes **`AwsLambdaNetworkComponent`** (its only surviving
   consumer).
5. Existing capability folders stay put this wave.

## Execution record (2026-08-14)

1. Executed directly on `origin/main` + split DNS (`3d29b0e`); the unpushed 0.10.48
   intermediate rename had already been reset away — the sweep mapped `AwsEcsExpress*` /
   `Lz.Aws.EcsExpress` straight to the New column above.
2. Ordered sweep ran collision-aware (Ecs-family generics renamed before the AppRunner-family
   renames took the freed names), with every `lz:aws:` literal sentinel-protected during the
   text sweep and byte-compared against `3d29b0e` afterwards; folder moves via `git mv`;
   freeze comments added at all 23 surviving component token sites (+ the AWS-managed-policy
   ARN site); shims + this doc finalized; `LzVersion` → **0.11.0**.
3. Lz gates: build 0 errors; tests 252/252 (baseline 253 — the deleted apprunner factory's
   `AppRunnerFactory_SystemPostDeploy_IsTheSystemTableEnsure` test removed with it; config
   round-trip tests updated from the deleted `AppRunner:` YAML block to `Fargate:`); grammar
   invariant grep empty except the one justified AWS-managed-policy ARN. Remaining gates:
   Scutara Deploy zero-CS0618 rebuild; `lz previewsystem` + `lz previewtenant` **"no
   changes"**; then per-system migrations in order Scutara → NTS → Monro →
   BC/MagicPets/Veritant, each behind its own zero-diff preview (Monro:
   dev/test/shared/**prod**, BC: dev+test).
