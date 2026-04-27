# Lz Tool — Design

## Architecture Overview

The Lz tool follows a layered architecture with clear separation between reusable infrastructure components, system-specific definitions, and environment-specific configuration.

```
┌──────────────────────────────────────────────────────────────────┐
│  Lzm NuGet Packages (shared across all systems)                  │
│                                                                  │
│  ┌──────────┐  ┌───────────┐  ┌────────────┐  ┌──────────────┐ │
│  │ Lz.Core  │  │  Lz.Aws   │  │ Lz.Azure   │  │   Lz.Cli     │ │
│  │          │  │           │  │            │  │              │ │
│  │ Config   │  │ AWS       │  │ Azure      │  │ dotnet tool  │ │
│  │ Interfaces│  │ Pulumi    │  │ Pulumi     │  │ Plugin loader│ │
│  │ Orchestr. │  │ Components│  │ Components │  │ ConfigResolve│ │
│  │ ILzPlugin │  │           │  │            │  │              │ │
│  └─────┬────┘  └─────┬─────┘  └──────┬─────┘  └──────┬───────┘ │
└────────┼─────────────┼───────────────┼────────────────┼──────────┘
         │             │               │                │
         │  ┌──────────┴───────────────┘                │
         │  │  (NuGet references)                       │
         v  v                                           │
┌──────────────────────────────────────┐                │
│  Deploy (system plugin — DLL)        │                │
│                                      │                │
│  ├── MonroSystem.cs  (hand-authored) │◄───────────────┘
│  └── MonroPlugin.cs  (ILzPlugin)     │  (convention discovery)
└──────────────────────────────────────┘
         │
         │  Config files in repo root:
         │  ├── systemconfig.med.dev.yaml
         │  ├── tenantconfig.med.meadows.dev.yaml
         │
         │  lz deploysystem  (zero flags needed)
         v
┌──────────────────────────────────────┐
│  Cloud Account (dev/test/prod)       │
│  ├── State bucket (S3 / Azure Blob)  │
│  └── Deployed resources              │
└──────────────────────────────────────┘
```

## Key Design Decisions

### D-1: Pulumi with Automation API (No CLI Dependency)

The Lz tool embeds Pulumi via the Automation API. Users never install or interact with the Pulumi CLI directly. The Lz tool programmatically creates workspaces, stacks, and executes deployments.

**Rationale:** Eliminates a runtime dependency, provides full control over the deployment workflow, and enables embedding prerequisite checks, phased deployment, and custom output formatting.

### D-2: Self-Hosted State Backends (No Pulumi Cloud)

State is stored in cloud-native object storage (S3, Azure Blob) with cloud-native encryption (KMS, Key Vault). No Pulumi SaaS dependency.

**Rationale:** Zero licensing cost, full data sovereignty, independent state per account/environment, team access controlled via existing IAM/RBAC.

### D-3: Platform x Topology Factory Pattern

Cloud platform (AWS, Azure) and deployment topology (ECS, Lambda, Container Apps) are independent axes, both resolved via the factory pattern. The orchestrator is agnostic to both.

**Rationale:** Adding a new platform or topology requires implementing components and a factory. No changes to interfaces, orchestration, or system definitions.

### D-4: YAML for Environment Config, C# for System Topology

YAML configuration is limited to values that vary per environment (sizing, domains, accounts, image tags, tenant lists). System topology (what services exist, how they route, what they depend on) is defined in C# code.

**Rationale:** YAML-based topology devolves into a custom infrastructure DSL — effectively CloudFormation with worse tooling. C# provides type safety, IDE support, real conditionals/loops, compile-time validation, and testability.

### D-5: State Isolation Per Account

Each deployment account (dev, test, prod) has its own S3 bucket / Azure Blob container holding Pulumi state. No shared state across accounts.

**Rationale:** Accounts may run different versions of the system. State isolation prevents cross-account dependencies and ensures independent lifecycle management.

### D-6: Phased Deployment with Prerequisite Checks

Deployments are split into phases that correspond to manual intervention points. Each phase validates prerequisites before starting and reports what manual steps are needed before the next phase.

**Rationale:** Some steps are fundamentally manual (Tailscale account creation, Keycloak OIDC configuration). The tool must accommodate this reality rather than pretending everything is automatable.

## Package Structure

### Lz.Core — Platform-neutral interfaces, config, orchestration scaffolding, plugin contract

`Lz.Core` speaks in shapes only — it knows nothing about AWS, Azure, or any
specific cloud target. Platform-specific types (AWS ECS/AppRunner sizing,
cross-account secret ARNs, Tailscale, Keycloak seeding) live in platform
libraries and are materialised via `IConfigExtensions` (see
`Design/TargetIsolation.md`).

```
Lz.Core/
├── Config/
│   ├── SystemConfig.cs              # Platform-neutral base; AWS fields live on AwsSystemConfig
│   ├── TenantConfig.cs              # Platform-neutral base; AWS fields live on AwsTenantConfig
│   ├── SharedConfig.cs              # Platform-neutral base; AWS fields live on AwsSharedConfig
│   ├── SubtenantEntry.cs            # Subtenant entry with SubDomain and Behaviors
│   ├── BehaviorsConfig.cs           # Behaviors hierarchy (APIs, Assets, WebApps)
│   ├── CdnConfig.cs                 # CDN section (shared between system/tenant)
│   ├── StateConfig.cs               # Pulumi backend, secrets provider
│   ├── SeedDataConfig.cs            # Seed-data bucket (generic object-storage shape)
│   ├── RuntimeConfig.cs             # Runtime sections (Integrations, AuthConfigs, RequestRewriter, etc.)
│   ├── ConfigLoader.cs              # YAML discovery + platform-gated deserialization
│   ├── ConfigResolver.cs            # Smart env/systemkey/tenant resolution (used by CLI + plugins)
│   ├── ConfigMerger.cs              # Generic merges (Profile/Region/CDN/SecretsManager/...)
│   ├── ConfigValidator.cs           # Post-deserialization validation of required config fields
│   └── IConfigExtensions.cs         # Platform hook for contributing YAML type mappings
├── Definitions/
│   ├── SystemDefinition.cs          # Base class for system definitions (UseAuth / UseVpn)
│   ├── ServiceDefinition.cs         # Describes a deployable service
│   ├── ContainerOptions.cs          # Container-specific options
│   ├── LambdaOptions.cs             # Lambda-specific options
│   ├── VolumeMount.cs               # File storage mount
│   ├── SecretRef.cs                 # Secret reference
│   ├── ApiRoute.cs                  # API path routing
│   ├── DockerBuildOptions.cs        # Docker build and push configuration
│   ├── AuthDefinition.cs            # Auth realm list (vendor-neutral)
│   └── CdnBehaviors.cs              # CDN behavior config
├── Interfaces/
│   ├── IPlatformFactory.cs          # Creates shape-named components only (no vendor capabilities)
│   ├── ISystemNetworkComponent.cs   # Network infrastructure
│   ├── IComputeEnvironmentComponent.cs  # Compute cluster / API gateway
│   ├── IServiceComponent.cs         # Deploy a service (container or function)
│   ├── IDatabaseComponent.cs        # Managed database
│   ├── IFileStorageComponent.cs     # File system / volumes
│   ├── IAuthServiceComponent.cs     # Auth service
│   ├── ITenantCdnComponent.cs       # CDN distribution
│   ├── ITenantDataComponent.cs      # Per-tenant data resources
│   ├── ITenantServiceComponent.cs   # Per-tenant dedicated service
│   ├── IEmailComponent.cs           # Email service
│   ├── ISeedTaskComponent.cs        # Container task for data seeding
│   ├── ITransitionChecker.cs        # Gate-check implementations
│   ├── IConfigInitRunner.cs        # Tenant DB + config-file initialization
│   ├── IPostSeedRunner.cs          # Re-writes config after seed restore
│   ├── IAdminSetupRunner.cs        # Creates internal admin account
│   ├── IPostDeployAction.cs        # Generic post-deploy hook
│   └── Outputs/
│       ├── INetworkOutputs.cs       # Network ID, subnet IDs, DNS zone IDs
│       ├── IComputeEnvironmentOutputs.cs  # Cluster ID, ingress endpoints
│       ├── IServiceOutputs.cs       # Service ID, endpoint URL
│       ├── IDatabaseOutputs.cs      # Endpoint, port, secret ID
│       ├── IFileStorageOutputs.cs   # File system ID
│       ├── ICdnOutputs.cs           # Distribution ID, domain, asset bucket
│       ├── IEmailOutputs.cs         # SMTP host, port, credential ID
│       ├── ITenantDataOutputs.cs    # Tenant secret, access points, DB name
│       ├── ISeedTaskOutputs.cs      # Task family, container-image repository URL
│       └── IAuthPoolOutputs.cs      # Per-pool auth details
├── Orchestration/
│   ├── PulumiPathResolver.cs        # Ensures Pulumi CLI is discoverable
│   ├── StackOutputReader.cs         # Reads Pulumi stack outputs
│   ├── TransitionGate.cs            # Gate-check logic for manual steps
│   └── TransitionRequirement.cs     # Declarative gate descriptor
├── Plugin/
│   └── ILzPlugin.cs                 # Plugin contract for system-specific DLLs
└── Validation/
    └── TopologyValidator.cs         # Validates system definition against topology
```

Deployment orchestrators (`SharedDeployment`, `SystemDeployment`) live in
`Lz.Aws/Orchestration/` because their phasing is AWS-ECS-shaped. When a
second platform lands, we'll factor out generic orchestration at that
point (see `Design/TargetIsolation.md` §"Forward-looking policy").

### Lz.Cli — dotnet Global Tool

```
Lz.Cli/
├── Lz.Cli.csproj                    # <PackAsTool>, <ToolCommandName>lz</ToolCommandName>
├── Program.cs                       # CLI entry point: deployshared, deploysystem,
│                                    #   deploytenant, destroy, status + plugin commands
└── PluginLoader.cs                  # Discovers plugin DLL (convention or lz.json)
```

`ConfigResolver` (env/systemkey/tenant resolution) lives in
`Lz.Core/Config/ConfigResolver.cs` so plugins — which reference Lz.Core but
not Lz.Cli — share the same discovery logic the CLI uses.

### Lz.Aws — AWS Pulumi components + AWS-shaped config, interfaces, orchestration

AWS-named vocabulary (Cognito, ECS, AppRunner, Keycloak, Tailscale, ACM,
Route 53, S3 seed bucket, gate-checker Lambda) lives here — not in
`Lz.Core`. `AwsConfigExtensions` registers YAML type mappings so
systemconfig/tenantconfig materialise as `AwsSystemConfig` /
`AwsTenantConfig` / `AwsSharedConfig` under the AWS platform.

```
Lz.Aws/
├── Config/
│   ├── AwsSystemConfig.cs           # Adds ECS/AppRunner/SharedSecretArn/TrustedAccountIds/...
│   ├── AwsTenantConfig.cs           # Adds ECS/AppRunner/AcmCertificateArn/HostedZoneId/...
│   ├── AwsSharedConfig.cs           # Adds Keycloak/Tailscale sizing/TrustedAccountIds
│   ├── AwsAuthConfigEntry.cs        # Cognito MFA/password/groups/advanced-security
│   ├── EcsConfig.cs                 # ECS deployment section
│   ├── AppRunnerConfig.cs           # AppRunner deployment section
│   ├── KeycloakSeedConfig.cs        # Keycloak realm/client/role/group seed model
│   ├── BootstrapCredsConfig.cs      # SMTP/Keycloak bootstrap credentials
│   ├── AwsConfigExtensions.cs       # IConfigExtensions: registers WithTypeMapping<...>
│   ├── AwsConfigMerger.cs           # AWS-specific merges (GetEffectiveEcsConfig)
│   ├── AwsKeycloakConfigLoader.cs   # camelCase Keycloak/creds YAML loaders
│   └── AwsConfigCast.cs             # .Aws() extension helpers for plugin authors
├── Interfaces/
│   ├── IAwsPlatformFactory.cs       # Extends IPlatformFactory with AWS-named capabilities
│   ├── ITailscaleComponent.cs       # Tailscale subnet router
│   ├── ITailscaleKeyManager.cs      # Tailscale auth-key lifecycle
│   ├── ITenantKeycloakSeeder.cs     # Per-tenant Keycloak realm seeder
│   ├── IGateCheckerComponent.cs     # VPC-internal Lambda gate checker
│   ├── IThemeDeployRunner.cs        # Keycloak theme EFS deploy
│   └── Outputs/
│       ├── ITailscaleOutputs.cs     # ASG ID
│       └── IGateCheckerOutputs.cs   # Lambda function name
├── Orchestration/
│   ├── SystemDeployment.cs          # Foundation + tenant orchestrator (AWS-ECS-shaped phases)
│   └── SharedDeployment.cs          # Shared-services (Keycloak + Tailscale) orchestrator
├── Ecs/                             # ECS + ALB topology
│   ├── AwsEcsPlatformFactory.cs     # Implements IAwsPlatformFactory
│   ├── AwsEcsNetworkComponent.cs    # VPC, subnets, NAT, ALBs, security groups, DNS, certs
│   ├── AwsEcsClusterComponent.cs    # ECS cluster, Cloud Map namespace
│   ├── AwsRdsComponent.cs           # RDS PostgreSQL + system secret
│   ├── AwsEfsComponent.cs           # EFS + mount targets
│   ├── AwsEcsServiceComponent.cs    # System-level ECS service
│   ├── AwsEcsTenantServiceComponent.cs  # Per-tenant dedicated ECS service
│   ├── AwsKeycloakEcsComponent.cs   # Keycloak ECS task + service + listener rules
│   ├── AwsCloudFrontComponent.cs    # CloudFront + S3 + OAC + DNS records
│   ├── AwsSesComponent.cs           # SES domain identity, DKIM, SMTP user, credentials
│   ├── AwsTenantDataComponent.cs    # Per-tenant EFS access points + tenant secret
│   ├── AwsTailscaleAsgComponent.cs  # Tailscale EC2 ASG subnet router
│   ├── AwsSeedTaskComponent.cs      # ECS task definition for data seeding
│   ├── AwsTransitionChecker.cs      # Gate-check via Secrets Manager / Lambda
│   ├── AwsFoundationPostDeployAction.cs  # DB init, Keycloak seed, OIDC secret storage
│   ├── AwsServicesPostDeployAction.cs    # Docker build/push/scale per-service
│   ├── AwsTenantKeycloakSeeder.cs   # Per-tenant realm seeder (ITenantKeycloakSeeder)
│   └── ...
├── AppRunner/                       # AppRunner topology (serverless containers)
├── EcsExpress/                      # ECS in public subnets (no NAT)
├── Lambda/                          # Gate-checker + theme-deploy Lambdas
├── Keycloak/                        # Keycloak admin client + seeder
├── Tailscale/                       # Tailscale API client + post-deploy
├── Docker/                          # Shared docker build/push helpers
├── DynamoDB/                        # Per-tenant table provisioning
├── Webapp/                          # Blazor WASM S3 bucket provisioning
├── Shared/                          # Cross-topology shared components
└── AwsStateBootstrapper.cs          # S3 bucket + KMS key bootstrap (idempotent)
```

### Lz.Azure — Azure Pulumi Components

```
Lz.Azure/
├── ContainerApps/                   # Container Apps topology
│   ├── AzureContainerAppsPlatformFactory.cs
│   ├── AzureNetworkComponent.cs     # VNet, subnets, NSGs
│   ├── AzureContainerAppsEnvComponent.cs  # Container App Environment
│   ├── AzureContainerAppComponent.cs      # Container App with built-in ingress
│   └── AzureKeycloakContainerAppComponent.cs
├── Functions/                       # Azure Functions topology
│   ├── AzureFunctionsPlatformFactory.cs
│   ├── AzureFunctionsComponent.cs
│   └── AzureApiManagementComponent.cs
├── Shared/
│   ├── AzurePostgresComponent.cs    # Azure Database for PostgreSQL Flexible Server
│   ├── AzureFilesComponent.cs       # Azure Files + file shares
│   ├── AzureFrontDoorComponent.cs   # Azure Front Door
│   ├── AzureDnsComponent.cs         # Azure DNS
│   ├── AzureKeyVaultComponent.cs    # Key Vault for secrets
│   └── AzureCommServicesComponent.cs # Azure Communication Services (email)
└── Auth/
    └── AzureCliValidator.cs         # Pre-flight az login check
```

## Interface Design

### Output Contracts

Interfaces define what each deployment unit produces. Downstream components consume these outputs without knowing which cloud or topology produced them.

```csharp
public interface INetworkOutputs
{
    Output<string> NetworkId { get; }
    Output<ImmutableArray<string>> PrivateSubnetIds { get; }
    Output<ImmutableArray<string>> PublicSubnetIds { get; }
    Output<string> PrivateDnsZoneId { get; }
    Output<string> PublicDnsZoneId { get; }
}

public interface IDatabaseOutputs
{
    Output<string> Endpoint { get; }
    Output<int> Port { get; }
    Output<string> AdminSecretId { get; }
}

public interface IComputeEnvironmentOutputs
{
    Output<string> ClusterId { get; }
    Output<string> PublicIngressEndpoint { get; }
    Output<string> InternalIngressEndpoint { get; }
}

public interface IServiceOutputs
{
    Output<string> ServiceId { get; }
    Output<string> Endpoint { get; }
}

public interface IFileStorageOutputs
{
    Output<string> FileSystemId { get; }
}

public interface ICdnOutputs
{
    Output<string> DistributionId { get; }
    Output<string> DomainName { get; }
    Output<string> AssetsBucketId { get; }
}

public interface IEmailOutputs
{
    Output<string> SmtpHost { get; }
    Output<int> SmtpPort { get; }
    Output<string> SmtpCredentialSecretId { get; }
    Output<string> FromDomain { get; }
}
```

### Component Interfaces

Each component accepts a system config and outputs from upstream components it depends on.

```csharp
public interface ISystemNetworkComponent
{
    INetworkOutputs Deploy(SystemConfig config);
}

public interface IDatabaseComponent
{
    IDatabaseOutputs Deploy(SystemConfig config, INetworkOutputs network);
}

public interface IFileStorageComponent
{
    IFileStorageOutputs Deploy(SystemConfig config, INetworkOutputs network);
}

public interface IComputeEnvironmentComponent
{
    IComputeEnvironmentOutputs Deploy(SystemConfig config, INetworkOutputs network);
}

public interface IServiceComponent
{
    IServiceOutputs Deploy(
        string serviceName,
        ServiceDefinition definition,
        INetworkOutputs network,
        IComputeEnvironmentOutputs compute,
        IDatabaseOutputs database,
        IFileStorageOutputs? fileStorage);
}

// enableAdminBlocking is derived at runtime from Tailscale state
// (tailscale-auth-key existence in Secrets Manager), not statically configured in YAML.
public interface IAuthServiceComponent
{
    IServiceOutputs Deploy(
        SystemConfig config,
        INetworkOutputs network,
        IComputeEnvironmentOutputs compute,
        IDatabaseOutputs database,
        IFileStorageOutputs fileStorage,
        bool enableAdminBlocking);
}

public interface ITenantCdnComponent
{
    ICdnOutputs Deploy(
        TenantConfig tenantConfig,
        IComputeEnvironmentOutputs compute,
        Output<string> certificateId);
}

public interface ITenantDataComponent
{
    ITenantDataOutputs Deploy(
        TenantConfig tenantConfig,
        IFileStorageOutputs systemFileStorage,
        IDatabaseOutputs database);
}

// Per-tenant service deployment — each tenant gets dedicated
// SmartStore + AppHost containers with isolated config
public interface ITenantServiceComponent
{
    IServiceOutputs Deploy(
        string serviceName,
        ServiceDefinition definition,
        TenantConfig tenantConfig,
        INetworkOutputs network,
        IComputeEnvironmentOutputs compute,
        IDatabaseOutputs database,
        ITenantDataOutputs tenantData);
}

public interface IEmailComponent
{
    IEmailOutputs Deploy(
        SystemConfig config,
        INetworkOutputs network);
}

// Extended outputs for tenant data — includes per-tenant EFS access points and secrets
public interface ITenantDataOutputs : IFileStorageOutputs
{
    Output<string> TenantSecretId { get; }        // consolidated tenant secret (ARN or Key Vault URI)
    Output<string> SmartStoreDataAccessPointId { get; }
    Output<string> SmartStoreConfigAccessPointId { get; }
    Output<string> AppHostConfigAccessPointId { get; }
    Output<string> DatabaseName { get; }           // per-tenant database name
}

```

### Platform Factory

The platform-neutral core factory carries only shape-named capabilities:

```csharp
// Lz.Core/Interfaces/IPlatformFactory.cs
public interface IPlatformFactory
{
    // Shape-named components
    ISystemNetworkComponent CreateNetwork();
    IDatabaseComponent CreateDatabase();
    IFileStorageComponent CreateFileStorage();
    IComputeEnvironmentComponent CreateComputeEnvironment();
    IServiceComponent CreateService();
    IAuthServiceComponent CreateAuthService();
    IEmailComponent CreateEmail();
    ITenantCdnComponent CreateTenantCdn();
    ITenantDataComponent CreateTenantData();
    ITenantServiceComponent CreateTenantService();

    // Shape-named optional components
    ISeedTaskComponent? CreateSeedTask();

    // Shape-named runners
    IConfigInitRunner? GetConfigInitRunner();
    IPostSeedRunner? GetPostSeedRunner();
    IAdminSetupRunner? GetAdminSetupRunner();

    // Post-deploy actions
    IPostDeployAction? GetFoundationPostDeployAction();
    IPostDeployAction? GetFoundationServiceDeployAction(SystemDefinition system);
    IPostDeployAction? GetServiceDeployAction(
        SystemDefinition system,
        IReadOnlyList<ServiceDefinition> services,
        string? tenantKey = null,
        TenantConfig? tenantConfig = null);

    // Misc
    ITransitionChecker CreateTransitionChecker();
    void DeployTenantDnsAndCert(TenantConfig tenant, INetworkOutputs network, ICdnOutputs? cdn = null);
    Task CleanupBeforeFoundationAsync() => Task.CompletedTask;

    (INetworkOutputs Network, IComputeEnvironmentOutputs Compute,
     IDatabaseOutputs Database, IFileStorageOutputs FileStorage)
        LookupFoundation(SystemConfig config);
}
```

AWS-named capabilities live on a platform-extended factory in `Lz.Aws`:

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

AWS orchestration code (`Lz.Aws/Orchestration/`) holds a
platform-neutral `IPlatformFactory` reference and casts to
`IAwsPlatformFactory` only when it needs the AWS-named capabilities:

```csharp
private IAwsPlatformFactory? AwsFactory => _factory as IAwsPlatformFactory;
// ...
var keyManager = AwsFactory?.GetTailscaleKeyManager();
```

Factory selection dispatches by platform, then delegates to the platform
library's topology registry:

```csharp
IPlatformFactory factory = config.Platform switch
{
    "aws"   => Lz.Aws.Topologies.AwsTopologies.Get(config.Topology).CreateFactory(config),
    "azure" => /* Lz.Azure.Topologies.AzureTopologies.Get(...)  — when Azure lands */,
    _ => throw new ArgumentException($"Unsupported platform: {config.Platform}")
};
```

## Topology Descriptors

A **topology** is a named, fixed bundle of cloud-component choices —
compute primitive, database, file storage, auth service, network shape,
VPN presence, etc. — that deploy as a coherent unit. Topology registries
live in platform libraries; each entry fully specifies what the topology
bundles, so readers don't have to archaeologize a factory body to
understand what `Topology: ecs-fargate-keycloak` means.

### AWS topologies

Defined in [`Lz.Aws/Topologies/AwsTopologies.cs`](Lz.Aws/Topologies/AwsTopologies.cs):

| Name | Compute | Data | File Storage | Auth | VPC | VPN | Central auth |
|---|---|---|---|---|---|---|---|
| `ecs-fargate-keycloak` | Fargate (private) | RDS | EFS | Keycloak | private | Tailscale | yes |
| `ecs-fargate-cognito-dynamodb` | Fargate (public) | DynamoDB | S3 | Cognito | public | — | — |
| `apprunner` | AppRunner | DynamoDB | S3 | Cognito | — | — | — |

Each descriptor carries:
- **Axis flags** (`Compute`, `Data`, `FileStorage`, `Auth`, `HasPrivateNetwork`,
  `UsesVpn`, `UsesCentralAuth`, `UsesInVpcGateChecker`, `UsesSeedTask`) so
  callers can ask "does this topology use VPN?" without pattern-matching
  the topology name.
- **`CreateFactory`** — the factory builder, invoked once per deployment
  after config load.
- **`ValidateConfig`** — optional topology-specific config validation (e.g.
  `ecs-fargate-keycloak` requires `VpcCidr` and `CentralAuthDomain`).
  Invoked by the CLI after systemconfig parsing, before factory construction.

Topologies are **not** composed from orthogonal axes. `AppRunner + EFS` is
not a real AWS topology (AppRunner can't mount EFS), so we don't pretend
axes are independent. Each descriptor pins every axis, and callers select
by name.

### Extending and overriding topologies in a plugin

Systems can contribute their own topologies from the `Deploy/` plugin via
the `ILzPlugin.RegisterTopologies()` hook. The CLI calls this once at
startup, before any factory is resolved.

Three patterns:

**Add a new topology** — plugin registers a descriptor under a name the
built-ins don't use.

```csharp
// in BCProjNew/Deploy/BCPlugin.cs
public void RegisterTopologies()
{
    AwsTopologies.Register(new AwsTopology
    {
        Name = "bc-custom",
        Summary = "BC-specific topology",
        // ... axis flags, CreateFactory, ValidateConfig ...
    });
}
```

**Derive from a built-in** — plugin reuses the axis flags of an existing
topology but wires different component implementations. Works well when
you subclass an existing `IAwsPlatformFactory` and override a few methods.

```csharp
// in BCProjNew/Deploy/BcCustomPlatformFactory.cs
public class BcCustomPlatformFactory : AwsEcsExpressPlatformFactory
{
    public BcCustomPlatformFactory(SystemConfig config) : base(config) { }

    public override ITenantCdnComponent CreateTenantCdn()
        => new BcCustomCloudFrontComponent();
}

// in BCProjNew/Deploy/BCPlugin.cs
public void RegisterTopologies()
{
    AwsTopologies.Register(AwsTopologies.DeriveFrom(
        AwsTopologies.EcsFargateCognitoDynamodb,
        name: "bc-fargate-cognito",
        summary: "BC variant with custom CloudFront KVS layout",
        createFactory: cfg => new BcCustomPlatformFactory(cfg)));
}
```

The `DeriveFrom` helper copies axis flags, description, and
`ValidateConfig` from the base; override any of them by passing the
optional parameter.

**Override a built-in by name** — intentionally replace what an existing
topology name means for this system. Requires `allowOverride: true` to
prevent accidental shadowing. Prefer `DeriveFrom` with a new name where
possible — an override means the YAML no longer tells the reader what
will actually deploy.

```csharp
AwsTopologies.Register(myReplacement, allowOverride: true);
```

To support subclassing, all `Create*` / `Get*` methods on the built-in
AWS platform factories (`AwsEcsPlatformFactory`,
`AwsAppRunnerPlatformFactory`, `AwsEcsExpressPlatformFactory`) are
`virtual`. Plugins override only the components they care to replace;
everything else inherits from the base factory.

## System Definition Model

### Base Class

```csharp
public abstract class SystemDefinition
{
    public List<ServiceDefinition> Services { get; } = new();
    public AuthDefinition? Auth { get; private set; }
    public bool UsesVpn { get; private set; }

    /// Transition gates checked after foundation Step 1 (Pulumi up),
    /// before Step 2 (post-deploy).
    public List<TransitionRequirement> FoundationInfraGates { get; } = new();

    /// Transition gates checked after foundation Step 2 (post-deploy),
    /// before Step 3 (second Pulumi up).
    public List<TransitionRequirement> FoundationGates { get; } = new();

    /// Services in the Service layer — deployed first within each tenant.
    public IReadOnlyList<ServiceDefinition> ServiceLayerServices
        => Services.Where(s => s.Layer == ServiceLayer.Service).ToList();

    /// Services in the Host layer — deployed after service-layer gates pass.
    public IReadOnlyList<ServiceDefinition> HostLayerServices
        => Services.Where(s => s.Layer == ServiceLayer.Host).ToList();

    public abstract void Define(SystemConfig config);

    protected ServiceDefinition AddService(string name, ServiceDefinition def)
    {
        def.Name = name;
        Services.Add(def);
        return def;
    }

    protected ServiceDefinition? GetService(string name)
        => Services.FirstOrDefault(s => s.Name == name);

    protected void UseAuth(string[] realms) { ... }
    protected void UseVpn() { ... }
}
```

### Service Definition

```csharp
public class ServiceDefinition
{
    public string Name { get; set; }
    public ServiceLayer Layer { get; set; } = ServiceLayer.Service;
    public IngressType IngressType { get; set; }
    public string HostPattern { get; set; }
    public bool RequiresDatabase { get; set; }
    public bool RequiresEmail { get; set; }
    public List<VolumeMount> Volumes { get; set; } = new();
    public List<SecretRef> Secrets { get; set; } = new();
    public List<ApiRoute> Routes { get; set; } = new();

    // Topology-specific options (components read what applies)
    public ContainerOptions? Container { get; set; }
    public LambdaOptions? Lambda { get; set; }

    /// Docker build configuration. If set, the service's post-deploy action
    /// builds and pushes the image to ECR automatically.
    public DockerBuildOptions? Docker { get; set; }

    /// Transition requirements that must be met before this service's
    /// post-deploy actions (build/push/scale) run.
    public List<TransitionRequirement> TransitionRequirements { get; set; } = new();
}

public class ContainerOptions
{
    public int Cpu { get; set; }
    public int Memory { get; set; }
    public int Port { get; set; }
    public string Protocol { get; set; } = "HTTP";
    public string HealthCheckPath { get; set; } = "/health";
}

public class LambdaOptions
{
    public int MemorySize { get; set; } = 512;
    public int Timeout { get; set; } = 30;
    public string? Handler { get; set; }
    public string Runtime { get; set; } = "dotnet8";
}
```

### Example System Definition (MonroSystem)

```csharp
// MonroSystem.cs — hand-authored, lives in Monro-New/Deploy/
// Implements SystemDefinition, loaded at runtime via the plugin system.
public class MonroSystem : SystemDefinition
{
    public override void Define(SystemConfig config)
    {
        // SmartStore — internal service behind VPN (shop.{domain})
        // Note: CPU/memory come from systemconfig ECS section (or tenantconfig override),
        // NOT hardcoded here. The system definition describes topology, not sizing.
        AddService("smartstore", new ServiceDefinition
        {
            Layer = ServiceLayer.Service,
            IngressType = IngressType.Internal,
            HostPattern = "shop.{domain}",
            RequiresDatabase = true,
            RequiresEmail = true,
            Container = new ContainerOptions
            {
                Port = 443, Protocol = "HTTPS",
                HealthCheckPath = "/health",
            },
            Volumes =
            {
                new VolumeMount("data", "/app/data", "/smartstore-data"),
                new VolumeMount("config", "/app/config", "/smartstore-config"),
            },
        });

        // AppHost — public-facing host service ({domain})
        AddService("apphost", new ServiceDefinition
        {
            Layer = ServiceLayer.Host,
            IngressType = IngressType.Public,
            HostPattern = "{domain}",
            Container = new ContainerOptions
            {
                Port = 80, Protocol = "HTTP",
                HealthCheckPath = "/health",
            },
            Volumes =
            {
                new VolumeMount("config", "/app/config", "/apphost-config"),
            },
            TransitionRequirements =
            {
                // Gates that must pass before AppHost can be deployed
                new TransitionRequirement { ... },
            },
        });

        // Auth and VPN (vendor-neutral — platform library decides what a realm is)
        UseAuth(new[] { "adminsauth", "usersauth" });
        UseVpn();

        // Foundation gates defined via FoundationInfraGates and FoundationGates
        // (e.g., systemconfig exists, keycloakconfig exists, SES credentials,
        //  tailscale-auth-key)
    }
}
```

### Example Plugin (MonroPlugin)

```csharp
// MonroPlugin.cs — implements ILzPlugin, also in Deploy/
public class MonroPlugin : ILzPlugin
{
    public SystemDefinition CreateSystemDefinition() => new MonroSystem();

    public void RegisterCommands(RootCommand root)
    {
        // Register system-specific CLI commands (e.g., seed data management)
        var seedCommand = new Command("seed", "Seed data management");
        // ... export, import, list subcommands
        root.AddCommand(seedCommand);
    }
}
```

Note: Behaviors (API paths, WebApp paths, Asset paths) come from the systemconfig/tenantconfig YAML — not from the C# system definition — because they follow the System → Tenant → Subtenant override hierarchy. The system definition defines the structural topology; the config files define the routing behaviors.

## Configuration Model

### Dual-File Architecture

The configuration model preserves the current separation between system-level and tenant-level config files. These files serve **dual purposes** — they drive both infrastructure deployment (consumed by the Lz tool) and runtime application behavior (consumed by running containers).

```
systemconfig.{systemkey}.{env}.yaml          ← one per system per environment
tenantconfig.{systemkey}.{tenantkey}.{env}.yaml  ← one per tenant per environment
```

**Critical convention:** SystemKey, TenantKey, and Environment are derived from the filename. They are NOT fields inside the file.

### systemconfig Schema

```yaml
# --- Deployment Settings (consumed by Lz tool) ---

# SystemSuffix ensures S3 bucket name uniqueness.
# Generate a GUID, use the middle section. Lowercase alphanumeric, max 9 chars.
SystemSuffix: "496a-ffff"

# Cloud credentials (AWS SSO profile)
Profile: "myproject-dev"
Region: "us-west-2"

# System domain — base domain for Route 53 zones, ACM certs, ALB rules, DNS records
SystemDomain: "myprojectdev.click"

# DefaultTenant — default tenancy for local development
DefaultTenant: "myprojectdev.click"

# Platform and topology — topology names are registered in Lz.Aws.Topologies.AwsTopologies
Platform: aws                            # aws (azure planned)
Topology: ecs-fargate-keycloak           # ecs-fargate-keycloak | ecs-fargate-cognito-dynamodb | apprunner

# Pulumi state — auto-generated from SystemSuffix at load time. Not in YAML.
# Generated:  s3://myproject-dev-pulumi-state-496a-ffff?region=us-west-2
#             awskms://alias/myproject-dev-pulumi-key-496a-ffff

# ECS Deployment Configuration
# Note: Admin blocking is derived at runtime from tailscale-auth-key existence
# in Secrets Manager — not configured here.
ECS:
  LogRetentionDays: 3
  DbInstanceClass: 'db.t4g.micro'
  DbAllocatedStorage: 20
  # DbMultiAZ: true                      # Enable for production
  KeycloakImageTag: '26.5.0'
  KeycloakCpu: 512
  KeycloakMemory: 1024
  TailscaleInstanceType: 't4g.nano'
  TailscaleDesiredCapacity: 2
  EnableEfsMountInstance: true
  SmartStoreCpu: 512
  SmartStoreMemory: 1024
  AppHostCpu: 256
  AppHostMemory: 512
  ServiceDesiredCount: 1

# CDN Configuration
CDN:
  PriceClass: PriceClass_100
  DefaultRootObject: app/index.html

# Central auth domain — shared Keycloak (in shared-services account)
CentralAuthDomain: "auth.meadowsservices.com"

# Behaviors — system-level routing rules (may be overridden per tenant/subtenant)
Behaviors:
  Apis:
    - Path: "/StoreApi"
      ApiName: "StoreApi"
    - Path: "/PublicApi"
      ApiName: "PublicApi"
    - Path: "/ConsumerApi"
      ApiName: "ConsumerApi"
  Assets:
    - Path: "/system/"
  WebApps:
    - Path: "/store/,/store,/"
      AppName: "storeapp"
    - Path: "/app/,/app,"
      AppName: "consumerapp"
    - Path: "/admin/,/admin,"
      AppName: "adminapp"

# Note: Tenants are NOT registered in systemconfig.
# They are discovered from tenantconfig files on disk.
# See "CLI Tool" section for the resolution logic.

# --- Runtime Application Settings (consumed by running containers) ---

AdminAuth: "adminsauth"
AdminEmail: "admin@example.com"

SecretsManager:
  SecretPrefix: "med"
  VerboseLogging: false

IntegrationSecretsPath: ""

Integrations:
  Services:
    store:
      Deployment: "cloud"
      Host: "shop.myprojectdev.click"
      DockerName: "smartstore"
      Scheme: "https"
      Description: "SmartStore e-commerce platform"
      Modules:
        - ShopModule
        - ShopRestModule
        - UtilModule
    localstore:
      Deployment: "local"
      Host: "localhost"
      Port: 7001
      DockerName: "smartstore"
      Scheme: "https"
      Description: "SmartStore e-commerce platform"
      Modules:
        - ShopModule
        - ShopRestModule
        - UtilModule
    auth:
      Deployment: "all"
      Host: "auth.myprojectdev.click"
      DockerName: "keycloak"
      Scheme: "https"
      Description: "Keycloak authentication server"

AuthConfigs:
  usersauth:
    HostedUIDomain: "https://myprojectdev.click/realms/usersauth"
    MetadataUrl: "https://myprojectdev.click/realms/usersauth/.well-known/openid-configuration"
    ClientId: "integration-tests"
    ValidateAudience: false
  adminsauth:
    HostedUIDomain: "https://auth.myprojectdev.click/realms/adminsauth"
    MetadataUrl: "https://auth.myprojectdev.click/realms/adminsauth/.well-known/openid-configuration"
    ValidateAudience: false
    ClientId: "integration-tests"

RequestRewriter:
  LogRewrites: false
  VerboseLogging: false
  PreserveOriginalPath: true
  Rules:
    - Name: "StripAppApi"
      MatchPrefix: "/AppApi"
      ReplaceWith: ""
      Enabled: true
      Order: 0
      StopOnMatch: true

RequestLogging:
  ExcludedPaths:
    - "/config"
    - "/health"

Authentication:
  VerboseLogging: false

ShopModuleAuth:
  VerboseLogging: false

UsersModuleAuth:
  VerboseLogging: false

Keycloak:
  VerboseLogging: false
```

### tenantconfig Schema

```yaml
# --- Deployment Settings (consumed by Lz tool) ---

# RootDomain — the tenant's root domain for Route 53, ACM certs, CDN
RootDomain: "myprojectdev.click"
# HostedZoneId: "<populate after Route 53 zone creation>"
# AcmCertificateArn: "<populate after CDN cert deployment>"

# Behaviors — tenant-level behavior overrides (optional)
# Behaviors:
#   Assets:
#     - Path: "/tenancy/"

# Subtenants — subtenant-specific overrides (optional)
# Subtenants:
#   uptown:
#     SubDomain: uptown
#     Behaviors:
#       Assets:
#         - Path: "/subtenancy/"

# TenantSuffix ensures S3 bucket name uniqueness per tenant.
TenantSuffix: "496a-ffff"

# Can override system-level credentials (optional)
Profile: "myproject-dev"
Region: "us-west-2"

# Per-tenant ECS deployment overrides
ECS:
  LogRetentionDays: 3
  EnableEfsMountInstance: true
  SmartStoreCpu: 512
  SmartStoreMemory: 1024
  AppHostCpu: 256
  AppHostMemory: 512
  ServiceDesiredCount: 1
  # --- Per-Tenant Resource Isolation ---
  # EFS paths auto-qualified by deployment:
  #   /{SystemKey}-{TenantKey}-{env}/smartstore-data, smartstore-config, apphost-config
  # Override only if custom paths needed:
  # EfsSmartStoreDataPath: "/custom/smartstore-data"
  # EfsSmartStoreConfigPath: "/custom/smartstore-config"
  # EfsAppHostConfigPath: "/custom/apphost-config"
  # DatabaseName: "custom-smartstore"
  #
  # ALB listener rule priorities — must be unique across all tenants on the system
  # ListenerPriorities:
  #   Auth: 111
  #   Realms: 113
  #   SmartStore: 120
  #   AppHost: 130
  #
  # Cloud Map service discovery names — must be unique within the namespace
  # SmartStoreServiceDiscoveryName: "mp-smartstore"
  # AppHostServiceDiscoveryName: "mp-apphost"

CDN:
  PriceClass: PriceClass_100
  DefaultRootObject: app/index.html

# --- Runtime Application Settings (consumed by running containers) ---

SecretsManager:
  SecretPrefix: "med/mp"               # {SystemKey}/{TenantKey}
  VerboseLogging: false

DefaultTenant: "myprojectdev.click"

# These sections mirror systemconfig and can override system defaults
Integrations:
  Services:
    # ... same structure as systemconfig, can override hosts/endpoints
    store:
      Deployment: "cloud"
      Host: "shop.myprojectdev.click"
      # ... etc.

AuthConfigs:
  # ... same structure as systemconfig, can override realm URLs
  usersauth:
    HostedUIDomain: "https://myprojectdev.click/realms/usersauth"
    # ... etc.

RequestRewriter:
  # ... same structure as systemconfig
  # Tenants can have different rewrite rules

# ... remaining runtime sections same as systemconfig
```

### How Deployment Uses Both Files

```
lz deploysystem
  → resolves env from folder hierarchy (_Dev → dev) or --env flag
  → discovers systemconfig.*.{env}.yaml (auto-selects if only one)
  → extracts: SystemSuffix, Profile, Region, SystemDomain, ECS section
  → deploys: VPC, RDS, EFS, ECS cluster
  → deploys runtime config to EFS (Integrations, AuthConfigs, etc.)

lz deploytenant
  → resolves env and systemkey (same as deploysystem)
  → discovers tenantconfig.{sk}.*.{env}.yaml files (deploys each)
  → reads tenantconfig for: RootDomain, TenantSuffix, ECS overrides, etc.
  → deploys: CloudFront, S3, DNS, per-tenant EFS access points, per-tenant ECS services
  → deploys tenant runtime config to tenant-specific EFS paths

lz deploytenant --tenantkey meadows
  → same resolution, but deploys only the specified tenant
```

### Configuration Boundaries

| Belongs in systemconfig YAML | Belongs in tenantconfig YAML | Belongs in C# (SystemDefinition) |
|---|---|---|
| SystemSuffix, Profile, Region | RootDomain, HostedZoneId, AcmCertificateArn | Which services exist |
| SystemDomain, DefaultTenant | TenantSuffix, Profile/Region overrides | How services connect (routes) |
| Database class, Keycloak image | Per-tenant EFS paths, DB name | Volume mount patterns |
| Tailscale config | Listener priorities, service discovery names | Ingress type (public/internal) |
| CDN defaults, Behaviors | Service CPU/memory overrides | Host patterns |
| State backend URL | CDN overrides, Behaviors overrides | Auth provider config |
| Platform, Topology | Subtenants (SubDomain, Behaviors) | Service layer ordering |
| CentralAuthDomain | Tenant-specific runtime config | Service dependencies |
| All runtime sections (defaults) | Runtime sections (overrides) | — |

## CLI Tool

The `lz` command is a .NET global tool (`Lz.Cli`) that provides zero-flag deployment when run from a properly structured repo. System-specific logic is loaded at runtime via a plugin.

### CLI Commands

```
lz deployshared                          # Deploy shared-services (Keycloak + Tailscale)
lz deploysystem [--systemkey] [--env] [--platform] [--topology]
lz deploytenant [--systemkey] [--env] [--tenantkey]
lz destroy --phase shared|foundation|tenant [--systemkey] [--env] [--tenantkey]
lz status [--systemkey] [--env]
lz seed export|import|list --tenant <key>   # Plugin-registered commands
```

### Config Resolution (ConfigResolver)

All commands that need a system config resolve **env**, **systemkey**, and **tenantkey** using a priority chain of explicit flags, folder hierarchy, and file discovery.

#### Environment Resolution

| Priority | Source | Example |
|---|---|---|
| 1 | `--env <value>` | `--env test` |
| 2 | Folder hierarchy walk | `_Dev/Monro-New/` → `dev` |

The folder hierarchy walk starts at the current working directory and traverses upward. The first directory whose name starts with `_Dev` (case-insensitive) maps to `dev`, `_Test` to `test`, `_Prod` to `prod`. If neither `--env` nor a matching folder is found, the command errors.

#### SystemKey Resolution

| Priority | Source | Example |
|---|---|---|
| 1 | `--systemkey <value>` | `--systemkey med` |
| 2 | File discovery: `systemconfig.*.{env}.yaml` | One match → auto-select; multiple → deploy all |

File discovery searches upward from the current directory for `systemconfig.*.{env}.yaml`. If a single file matches, its system key is used automatically. If multiple match, the command deploys all of them (e.g., `deploysystem` deploys each system). If none match, the command errors.

#### TenantKey Resolution (deploytenant only)

| Priority | Source | Example |
|---|---|---|
| 1 | `--tenantkey <value>` | `--tenantkey meadows` |
| 2 | File discovery: `tenantconfig.{sk}.*.{env}.yaml` | All matches are deployed |

File discovery searches upward for `tenantconfig.{systemkey}.*.{env}.yaml`. All matching tenant config files are loaded and deployed. If `--tenantkey` is provided, only that specific tenant is deployed.

#### Example: Zero-Flag Deployment

```
_Dev/
  Monro-New/
    systemconfig.med.dev.yaml
    tenantconfig.med.meadows.dev.yaml
    tenantconfig.med.acme.dev.yaml
    Deploy/
```

From `_Dev/Monro-New/`:

```bash
lz deploysystem                # env=dev (from _Dev), sk=med (only match), deploys foundation
lz deploytenant                    # env=dev, sk=med, deploys meadows + acme
lz deploytenant --tenantkey meadows  # env=dev, sk=med, deploys only meadows
lz status                          # env=dev, sk=med, shows foundation + all tenant status
```

### Plugin Architecture

The `lz` tool discovers system-specific plugins by searching upward from the current working directory. Two discovery mechanisms are supported:

1. **Convention (default):** A `Deploy/` folder containing a built plugin DLL at `Deploy/bin/{Debug|Release}/net9.0/Deploy.dll`. No marker file needed.

2. **Explicit (lz.json):** A `lz.json` marker file pointing to a custom plugin DLL path:
   ```json
   {"plugin": "MyPlugin/bin/Debug/net9.0/MyPlugin.dll"}
   ```
   The DLL path is relative to the `lz.json` file.

Convention-based discovery means most repos need only a `Deploy/` folder — no `lz.json` required. The loader uses `AssemblyDependencyResolver` to resolve plugin-specific NuGet dependencies (e.g., AWSSDK.S3) that the host doesn't bundle.

**Plugin contract (`ILzPlugin`):**
```csharp
public interface ILzPlugin
{
    SystemDefinition CreateSystemDefinition();
    void RegisterCommands(RootCommand root);    // e.g., seed export/import/list
}
```

Core commands (`deployshared`, `deploysystem`, `deploytenant`, `destroy`, `status`) are built into `Lz.Cli` and require a plugin only for the `PrepareSystem` step (creating the `SystemDefinition`). The `deployshared` command does not require a plugin.

## Deployment Orchestration

### Stack Structure

Deployments are organized into Pulumi stacks based on lifecycle and manual intervention boundaries:

```
Stack: shared-services
  Config source: sharedconfig.yaml
  Resources: VPC, ECS cluster, RDS, Keycloak, Tailscale
  Prerequisites: None (first phase)

Stack: {systemKey}-{env}
  Config source: systemconfig.{systemkey}.{env}.yaml
  Resources: VPC, ECS cluster, RDS, EFS, Tailscale
  Prerequisites: Shared-services deployed
  Manual steps: SES, Tailscale auth key — handled via transition gates

Stack: {systemKey}-{tenantKey}-{env}  (one per tenant)
  Config source: tenantconfig.{systemkey}.{tenantkey}.{env}.yaml
  Resources: CDN, S3, DNS, per-tenant data (EFS access points, DB, secrets),
             per-tenant SmartStore + AppHost ECS services (dedicated per tenant),
             per-tenant IAM roles, per-tenant service discovery entries,
             tenant runtime config files
  Prerequisites: Foundation stack deployed
  Manual steps: EFS/DB seeding, SmartStore config — handled via transition gates

Note: Each tenant gets its own dedicated SmartStore and AppHost ECS services
with unique listener rule priorities, service discovery names, EFS access points,
consolidated secrets, and IAM roles. This provides full tenant isolation at the
compute layer — tenants do not share application containers.
```

### Single-Operation Deployment Model

`SystemDeployment` handles one operation at a time. The CLI is responsible for iterating over multiple systems and tenants.

```csharp
/// Deployment orchestrator for a single system.
/// The CLI is responsible for iterating over multiple systems and tenants —
/// this class handles one at a time.
public class SystemDeployment
{
    public SystemDeployment(IPlatformFactory factory, SystemDefinition system, SystemConfig config);

    // Foundation: two pulumi ups with transition gates between them
    public async Task DeployFoundationAsync();

    // Tenant: two pulumi ups with transition gates between them
    public async Task DeployTenantAsync(string tenantKey, TenantConfig tenantConfig);

    // Destroy
    public async Task DestroyFoundationAsync();
    public async Task DestroyTenantAsync(string tenantKey);

    // Status
    public async Task StatusFoundationAsync();
    public async Task StatusTenantAsync(string tenantKey);
}
```

The CLI drives iteration:

```csharp
// deploysystem handler — iterates all discovered system configs
var resolvedEnv = ConfigResolver.ResolveEnvironment(env);
var configs = ConfigResolver.ResolveSystemConfigs(resolvedEnv, systemKey);

foreach (var config in configs)
{
    var (system, factory) = PrepareSystem(plugin!, config);
    var deployment = new SystemDeployment(factory, system, config);
    await deployment.DeployFoundationAsync();
}

// deploytenant handler — iterates all discovered tenant configs per system
foreach (var config in configs)
{
    var (system, factory) = PrepareSystem(plugin!, config);
    var deployment = new SystemDeployment(factory, system, config);

    var tenants = ConfigResolver.ResolveTenantConfigs(
        config.SystemKey, config.Environment, tenantKey);

    foreach (var (tk, tenantConfig) in tenants)
        await deployment.DeployTenantAsync(tk, tenantConfig);
}
```

## Secrets Architecture

### Principle: Minimize Secrets in Pulumi State

| Pattern | Secrets in State? | Use When |
|---|---|---|
| Cloud-managed secrets (RDS ManageMasterUserPassword) | No — only ARN | Preferred for database passwords |
| External secrets (Tailscale auth key) | No — only ARN | Human-created secrets |
| Pulumi-generated secrets (Keycloak admin) | Yes — encrypted | When no cloud-managed alternative |

### Secret Flow

```
Deployment time (Pulumi):
  Creates RDS with ManageMasterUserPassword = true
  → RDS creates its own Secrets Manager secret
  → Pulumi state stores only the secret ARN
  → ARN passed to ECS task definition as secret reference

Runtime (Application):
  ECS task starts → fetches password from Secrets Manager → connects to RDS
```

## Cloud Resource Mapping

### AWS ECS Topology

| Concept | AWS Resources Created |
|---|---|
| **System-Level** | |
| Network | VPC, 4 subnets, IGW, NAT, 6 security groups, route tables |
| Compute | ECS Cluster, Cloud Map namespace |
| Database | RDS PostgreSQL, DB subnet group, Secrets Manager secret |
| File Storage | EFS, mount targets |
| System Services (default pair) | SmartStore + AppHost: task definitions, ECS services, target groups, listener rules (priority 20/30), service discovery, log groups |
| Auth (Keycloak) | Task definition, ECS service, 2 target groups, 4 listener rules |
| Email | SES identity, 3 DKIM records, DMARC record, SMTP user, SMTP secret |
| VPN | EC2 launch template, Tailscale ASG (2 instances), EFS mount instance |
| Certificates | ACM certificate (main region), ACM certificate (us-east-1 for CDN) |
| **Per-Tenant** (repeated for each tenant) | |
| Tenant CDN | CloudFront distribution, S3 bucket, OAC, bucket policy, DNS records |
| Tenant Data | Per-tenant EFS access points (3: data, config, apphost-config), consolidated tenant secret, SES identity, DKIM/DMARC records, DB init task |
| Tenant Services | Per-tenant SmartStore + AppHost: dedicated task definitions, ECS services, target groups, listener rules (configurable priorities), service discovery entries, IAM roles, log groups, init task |
| Tenant Auth (conditional) | Per-tenant ALB listener rules for auth domain routing (if tenant domain differs from system domain) |
| Tenant Infra (optional) | Per-tenant EFS mount instance for debug access |

### AWS Lambda Topology

| Concept | AWS Resources Created |
|---|---|
| Network | Minimal — Route 53 zone (VPC optional for RDS access) |
| Compute | API Gateway HTTP API |
| Database | RDS PostgreSQL (same as ECS) |
| File Storage | Not supported |
| Service (each) | Lambda function, API Gateway integration + route, CloudWatch log group |
| Auth (Keycloak) | Same as ECS (Keycloak cannot run as Lambda) |
| Tenant CDN | CloudFront distribution (same as ECS) |

### Azure Container Apps Topology

| Concept | Azure Resources Created |
|---|---|
| Network | VNet, subnets, NSGs |
| Compute | Container App Environment (includes built-in ingress) |
| Database | Azure Database for PostgreSQL Flexible Server, Key Vault secret |
| File Storage | Storage Account, Azure Files shares |
| Service (each) | Container App (ingress configured on the app itself) |
| Auth (Keycloak) | Container App |
| Tenant CDN | Front Door profile + endpoint, Blob Storage, DNS records |
| Email | Azure Communication Services |

## Topology Validation

Components validate that the system definition is compatible with the selected topology at deployment time:

```csharp
public class TopologyValidator
{
    public ValidationResult Validate(SystemDefinition system, string topology)
    {
        var errors = new List<string>();

        foreach (var service in system.Services)
        {
            if (topology == "lambda" && service.Volumes.Any())
                errors.Add($"Service '{service.Name}' defines volumes, " +
                           "which are not supported in the Lambda topology.");

            if (topology == "lambda" && service.Container != null && service.Lambda == null)
                errors.Add($"Service '{service.Name}' has ContainerOptions but no LambdaOptions. " +
                           "Lambda topology requires LambdaOptions.");
        }

        return new ValidationResult(errors);
    }
}
```
