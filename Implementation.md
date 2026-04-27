# Lz Tool — Implementation Plan

## Solution Structure

```
Lzm repo (published as NuGet packages + dotnet global tool):

  Lzm/
  ├── Lz.slnx
  ├── LzVersion.props                       # Single shared version for all Lz.* packages
  ├── Lz.Core/                              # Platform-neutral: speaks in shapes, never AWS/Azure
  │   ├── Lz.Core.csproj                    # net9.0
  │   ├── Config/
  │   │   ├── SystemConfig.cs               # Base; AWS fields live on AwsSystemConfig
  │   │   ├── TenantConfig.cs               # Base; AWS fields live on AwsTenantConfig
  │   │   ├── SharedConfig.cs               # Base; AWS fields live on AwsSharedConfig
  │   │   ├── SubtenantEntry.cs             # Subtenant entry with SubDomain and Behaviors
  │   │   ├── BehaviorsConfig.cs            # Behaviors hierarchy (APIs, Assets, WebApps)
  │   │   ├── CdnConfig.cs                  # CDN section (generic)
  │   │   ├── StateConfig.cs                # Pulumi backend + secrets provider
  │   │   ├── SeedDataConfig.cs             # Seed-bucket shape
  │   │   ├── RuntimeConfig.cs              # Runtime sections (AuthConfigs, Integrations, etc.)
  │   │   ├── ConfigLoader.cs               # Platform-gated YAML discovery + parsing
  │   │   ├── ConfigMerger.cs               # Generic merges only (AWS merges live in AwsConfigMerger)
  │   │   ├── ConfigValidator.cs            # Required-field validation
  │   │   └── IConfigExtensions.cs          # Platform hook for contributing type mappings
  │   ├── Definitions/                      # SystemDefinition (UseAuth/UseVpn), ServiceDefinition, etc.
  │   ├── Interfaces/                       # IPlatformFactory (shape-named only) + component contracts
  │   ├── Orchestration/                    # PulumiPathResolver, StackOutputReader, TransitionGate
  │   ├── Plugin/
  │   │   └── ILzPlugin.cs                  # Plugin contract for system-specific DLLs
  │   └── Validation/                       # TopologyValidator
  ├── Lz.Aws/                               # AWS-specific: ECS/AppRunner, Cognito, Tailscale, Keycloak
  │   ├── Lz.Aws.csproj                     # net9.0, depends on Lz.Core + Pulumi.Aws
  │   ├── Config/
  │   │   ├── AwsSystemConfig.cs            # ECS/AppRunner/SharedSecretArn/TrustedAccountIds/...
  │   │   ├── AwsTenantConfig.cs            # ECS/AppRunner/AcmCertificateArn/HostedZoneId/...
  │   │   ├── AwsSharedConfig.cs            # Keycloak/Tailscale sizing/TrustedAccountIds
  │   │   ├── AwsAuthConfigEntry.cs         # Cognito MFA/password/groups/advanced-security
  │   │   ├── EcsConfig.cs                  # ECS deployment section
  │   │   ├── AppRunnerConfig.cs            # AppRunner deployment section
  │   │   ├── KeycloakSeedConfig.cs         # Keycloak seed model
  │   │   ├── BootstrapCredsConfig.cs       # SMTP/Keycloak bootstrap creds
  │   │   ├── AwsConfigExtensions.cs        # IConfigExtensions: registers all AWS type mappings
  │   │   ├── AwsConfigMerger.cs            # GetEffectiveEcsConfig and other AWS merges
  │   │   ├── AwsKeycloakConfigLoader.cs    # camelCase Keycloak/creds YAML loaders
  │   │   └── AwsConfigCast.cs              # .Aws() extension helpers
  │   ├── Interfaces/                       # IAwsPlatformFactory + AWS-named capabilities
  │   ├── Orchestration/                    # SystemDeployment, SharedDeployment (AWS-ECS-shaped)
  │   ├── Ecs/                              # ECS + ALB topology components
  │   ├── AppRunner/                        # AppRunner topology components
  │   ├── EcsExpress/                       # ECS in public subnets (no NAT)
  │   ├── Lambda/                           # Gate-checker + theme-deploy Lambdas
  │   ├── Keycloak/                         # Keycloak admin client + seeder
  │   ├── Tailscale/                        # Tailscale API client + post-deploy
  │   ├── Docker/                           # Shared docker build/push helpers
  │   ├── DynamoDB/                         # Per-tenant table provisioning
  │   ├── Webapp/                           # Blazor WASM S3 bucket provisioning
  │   └── Shared/                           # Cross-topology shared components
  ├── Lz.Azure/
  │   └── Stubs/AzureStubPlatformFactory.cs # IPlatformFactory stub — throws NotImplementedException
  ├── Lz.Cli/
  │   ├── Lz.Cli.csproj                     # dotnet global tool: <PackAsTool>, <ToolCommandName>lz
  │   ├── Program.cs                        # CLI: deployshared, deploysystem, deploytenant, ...
  │   └── PluginLoader.cs                   # Discovers plugin DLL (convention or lz.json)
  ├── Lz.Runner/                            # Thin dispatcher — resolves newest Lz.Cli.*.nupkg from NuGet feeds
  ├── Lz.Gen/                               # Model-driven code generation (ported from LazyMagicMDD)
  └── Lz.Tests/
      ├── Lz.Tests.csproj
      ├── Config.Tests/                     # YAML parsing + derived-type round-trip tests
      ├── Orchestration.Tests/              # Unit tests with mocked components
      └── Validation.Tests/                 # Topology validation tests


System repo (system-specific plugin — loaded at runtime by Lz.Cli):

  Monro-New/
  ├── Service/                             # Application code (existing)
  ├── Deploy/
  │   ├── Deploy.csproj                    # Library, references Lz.Core + Lz.Aws
  │   ├── MonroPlugin.cs                   # ILzPlugin implementation (seed commands)
  │   └── MonroSystem.cs                   # Hand-authored system definition
  │
  │   # Config files in repo root:
  ├── systemconfig.med.dev.yaml            # System config: dev environment
  ├── tenantconfig.med.meadows.dev.yaml    # Tenant config: tenant "meadows", dev
  └── ...
```

## NuGet Package Dependencies

```
Lz.Core
  ├── Pulumi                              # Core Pulumi SDK
  ├── Pulumi.Automation                   # Automation API
  ├── YamlDotNet                          # YAML parsing
  └── System.CommandLine                  # For ILzPlugin.RegisterCommands(RootCommand)

Lz.Aws
  ├── Lz.Core
  ├── Pulumi.Aws                          # AWS Classic provider
  └── Pulumi.Random                       # Random password generation

Lz.Azure
  ├── Lz.Core
  ├── Pulumi.AzureNative                  # Azure Native provider
  └── Pulumi.Random

Lz.Cli (dotnet global tool — installed as "lz")
  ├── Lz.Core
  ├── Lz.Aws                             # Bundled platforms
  ├── Lz.Azure
  └── System.CommandLine                  # CLI argument parsing

Deploy (system plugin — loaded at runtime via convention or lz.json)
  ├── Lz.Core                            # Project reference during development
  ├── Lz.Aws                             # Project reference during development
  ├── System.CommandLine                  # For RegisterCommands
  └── AWSSDK.S3                           # Plugin-specific dependency (resolved via AssemblyDependencyResolver)
```

## Implementation Phases

### Phase 1: Foundation — Lz.Core + Minimal AWS ECS

**Goal:** Deploy a VPC, RDS, EFS, and a single ECS service using the new architecture. Prove the interface model and Pulumi Automation API integration work end-to-end.

**Deliverables:**

1. **Lz.Core package:**
   - `SystemConfig` model — full schema matching current `systemconfig.{systemkey}.{env}.yaml` including deployment sections (ECS, CDN, State, Behaviors, Tenants registry) and runtime sections (Integrations, AuthConfigs, RequestRewriter, etc.)
   - `TenantConfig` model — full schema matching current `tenantconfig.{systemkey}.{tenantkey}.{env}.yaml` including per-tenant deployment overrides (EFS paths, listener priorities, service discovery names) and runtime section overrides
   - `TenantConfig` includes deployment fields: `RootDomain`, `HostedZoneId`, `AcmCertificateArn`, `Behaviors`, `Subtenants` (no separate registry entry — tenants are discovered from files)
   - `ConfigLoader` — YAML discovery via directory traversal, filename convention parsing (`systemconfig.{systemkey}.{env}.yaml`, `tenantconfig.{systemkey}.{tenantkey}.{env}.yaml`), derives SystemKey/TenantKey/Environment from filenames
   - `ConfigMerger` — merges system runtime defaults with tenant overrides to produce effective runtime config per tenant
   - `SystemDefinition`, `ServiceDefinition` base classes
   - `IPlatformFactory` and all component/output interfaces
   - `SystemDeployment` with single-phase orchestration (no phases yet)

2. **Lz.Aws package (ECS only):**
   - `AwsEcsNetworkComponent` — VPC, 4 subnets, IGW, NAT, security groups
   - `AwsRdsComponent` — PostgreSQL with managed master password
   - `AwsEfsComponent` — EFS + mount targets + access points
   - `AwsEcsClusterComponent` — ECS cluster + Cloud Map
   - `AwsEcsServiceComponent` — single service deployment (task def, service, TG, listener rule)
   - `AwsEcsPlatformFactory` — wires it all together

3. **Deploy project (minimal):**
   - `SystemDefinition.cs` defining one service (e.g., AppHost)
   - `systemconfig.{systemkey}.dev.yaml` (adapted from current `systemconfig.med.dev.yaml` with new State/Platform/Topology sections)
   - `tenantconfig.{systemkey}.{tenantkey}.dev.yaml` (adapted from current tenant config)
   - `Program.cs` with basic CLI

4. **State infrastructure:**
   - Bootstrap script to create S3 bucket + KMS key for dev account

**Validation:**
   - `lz deploy` from the Deploy project creates real AWS resources in dev account
   - `lz deploy` again is a no-op (idempotent)
   - `lz destroy` tears down cleanly
   - State file in S3 is inspectable and secrets are encrypted

**Key risks to retire:**
   - Pulumi Automation API works embedded in a .NET tool without the Pulumi CLI installed
   - AWS SSO profile credentials pass through to Pulumi correctly
   - S3 state backend works with SSO-based credentials
   - Cross-resource references (VPC ID → subnet → RDS) resolve correctly

### Phase 2: Full ECS Topology

**Goal:** Deploy the complete tenant application system on AWS ECS, matching current CloudFormation capability.

**Deliverables:**

1. **Additional Lz.Aws components:**
   - `AwsKeycloakEcsComponent` — Keycloak task def, service, dual TG, listener rules
   - `AwsCloudFrontComponent` — distribution, S3, OAC, response headers policy
   - `AwsCdnCertComponent` — ACM cert in us-east-1 (multi-region provider)
   - `AwsSesComponent` — domain identity, DKIM, DMARC, SMTP credentials
   - `AwsTailscaleComponent` — launch template, ASG, EFS mount instance
   - `AwsTenantDataComponent` — per-tenant EFS access points (3 per tenant), consolidated tenant secret, per-tenant SES identity, per-tenant DB init task
   - `AwsEcsTenantServiceComponent` — per-tenant dedicated SmartStore + AppHost ECS services, each with unique listener priorities, service discovery names, IAM roles, tenant-specific EFS mounts, tenant-specific secrets, per-tenant log groups, init task
   - `AwsTenantAuthComponent` — per-tenant ALB listener rules for auth domain routing (conditional on tenant domain differing from system domain)

2. **Phased deployment:**
   - `DeploymentPhase` definitions for foundation → services → tenants
   - `PrerequisiteChecker` — validates Tailscale auth key, Keycloak health
   - Phase-aware CLI: `lz deploy --phase foundation`

3. **Multi-tenant support:**
   - Per-tenant Pulumi stacks: `{systemKey}-tenant-{tenantKey}`
   - Each tenant stack deploys: CDN, tenant data (EFS access points, secrets, SES), tenant auth (conditional), **dedicated SmartStore + AppHost ECS services** with tenant-isolated config
   - Tenant services have unique listener priorities, service discovery names, IAM roles, and EFS mount points — driven by `tenantconfig.{systemkey}.{tenantkey}.{env}.yaml`
   - `lz deploy tenant mp` command
   - `lz deploy tenants` (all tenants)

4. **Cross-stack references:**
   - Foundation stack exports → services stack imports
   - Foundation stack exports → tenant stack imports

5. **Full system definition:**
   - SmartStore + AppHost services
   - API routes, CDN behaviors, auth, VPN
   - All volume mounts and secret references

**Validation:**
   - Full deployment matches current CloudFormation deployment functionally
   - Phased deployment works with manual Tailscale step in between
   - Adding a new tenant deploys: CDN + data + auth + **dedicated SmartStore + AppHost ECS services** — no impact on other tenants or system stacks
   - Each tenant's SmartStore and AppHost run as independent ECS services with isolated secrets, EFS paths, and listener rules
   - Updating a service image tag updates that tenant's ECS services without affecting other tenants
   - Listener priorities across tenants do not conflict (validated from tenantconfig)

### Phase 3: CloudFormation Migration

**Goal:** Migrate existing CloudFormation-managed environments to Pulumi state without downtime.

**Deliverables:**

1. **Import tooling:**
   - `lz import` command that reads existing CloudFormation stack outputs
   - Maps physical resource IDs to Pulumi URNs
   - Imports resources into Pulumi state via `pulumi import`

2. **Migration scripts:**
   - Per-stack import scripts (foundation resources, service resources, tenant resources)
   - Validation step: `pulumi preview` shows no changes after import

3. **Documentation:**
   - Step-by-step migration runbook
   - Rollback procedure (delete Pulumi state, CloudFormation stacks still intact)

**Approach:**
   - Pulumi can import existing resources: `pulumi import aws:ec2/vpc:Vpc system-vpc vpc-0a1b2c3d4e5f`
   - After import, `pulumi preview` should show no diff
   - Then delete CloudFormation stacks (resources are now Pulumi-managed)
   - CloudFormation deletion with `--retain-resources` keeps resources alive

**Risk mitigation:**
   - Test migration in dev account first
   - CloudFormation stacks remain intact until Pulumi import is validated
   - Resources are never deleted — ownership transfers from CF to Pulumi

### Phase 4: Lambda Topology

**Goal:** Support Lambda + API Gateway as an alternative AWS topology.

**Deliverables:**

1. **Lz.Aws Lambda components:**
   - `AwsLambdaPlatformFactory`
   - `AwsLambdaNetworkComponent` — minimal network
   - `AwsApiGatewayComponent` — HTTP API creation
   - `AwsLambdaServiceComponent` — Lambda function + API Gateway integration
   - `AwsNullFileStorage` — no-op (Lambda doesn't support volumes)

2. **Topology validation:**
   - `TopologyValidator` — checks service definitions against Lambda constraints
   - Clear error messages for unsupported features (volumes, internal ingress)

3. **LazyMagicMDD integration:**
   - Generate `LambdaOptions` in `{SystemName}System.g.cs` when Lambda topology is configured
   - Generate Lambda handler entry points

4. **System definition updates:**
   - `ServiceDefinition.Lambda` options on each service
   - Dual topology support in the system definition

**Validation:**
   - Same system definition deploys to ECS or Lambda based on `Topology:` config
   - Lambda deployment creates functions and API Gateway routes
   - API endpoints return same responses as ECS deployment

### Phase 5: Azure Support

**Goal:** Deploy the application to Azure using Container Apps.

**Deliverables:**

1. **Lz.Azure package:**
   - `AzureContainerAppsPlatformFactory`
   - `AzureNetworkComponent` — VNet, subnets, NSGs
   - `AzureContainerAppsEnvComponent` — Container App Environment
   - `AzureContainerAppComponent` — Container App with ingress
   - `AzureKeycloakContainerAppComponent`
   - `AzurePostgresComponent` — Flexible Server
   - `AzureFilesComponent` — Storage Account + file shares
   - `AzureFrontDoorComponent` — Front Door profile + endpoint
   - `AzureDnsComponent` — DNS zone + records
   - `AzureKeyVaultComponent` — secrets
   - `AzureCommServicesComponent` — email

2. **Azure state backend:**
   - Bootstrap script for Azure Blob container + Key Vault key

3. **Azure authentication:**
   - `AzureCliValidator` — pre-flight `az login` check
   - Support for Entra ID / service principal authentication

4. **Cross-cloud config:**
   ```yaml
   Platform: azure
   Topology: containers
   Azure:
     SubscriptionId: abc-123
     TenantId: def-456
     Location: eastus
   State:
     Backend: azblob://pulumistate
     SecretsProvider: azurekeyvault://{systemkey}-vault.vault.azure.net/keys/pulumi
   ```

**Validation:**
   - Same system definition (unchanged) deploys to Azure
   - Keycloak, SmartStore, AppHost all running on Container Apps
   - PostgreSQL, file storage, CDN, DNS all functional
   - Tenant isolation works on Azure

### Phase 6: LazyMagicMDD Integration

**Goal:** LazyMagicMDD generates system definition code alongside application code.

**Deliverables:**

1. **New generation artifact: `LzSystemDefinition`**
   - Reads LazyMagic.yaml container/module/schema directives
   - Generates `{SystemName}System.g.cs` (partial class) with:
     - Service definitions (name, routes, port)
     - Container options (CPU, memory from directive config)
     - Lambda options (handler, memory from directive config)

2. **Directive extensions:**
   - Optional `Lz` section in LazyMagic.yaml directives:
     ```yaml
     ConsumerContainer:
       Type: Container
       Modules: [ConsumerModule]
       Artifacts:
         AspDotNetProject:
           NameSuffix: "Service"
         LzSystemDefinition:          # NEW
           IngressType: Public
           HostPattern: "{domain}"
           RequiresDatabase: true
     ```

3. **CLI integration:**
   - `lz generate` invokes LazyMagicMDD pipeline
   - `lz deploy` can optionally run generation first

## Detailed Component Implementation

### AwsEcsNetworkComponent

Maps to current `sam.system.yaml` network resources.

```csharp
public class AwsEcsNetworkComponent : ComponentResource, ISystemNetworkComponent
{
    public AwsEcsNetworkComponent() : base("lz:aws:Network", "network") { }

    public INetworkOutputs Deploy(SystemConfig config)
    {
        // VPC
        var vpc = new Aws.Ec2.Vpc($"{config.SystemKey}-vpc", new()
        {
            CidrBlock = config.VpcCidr ?? "10.0.0.0/16",
            EnableDnsSupport = true,
            EnableDnsHostnames = true,
            Tags = StandardTags(config, "vpc"),
        }, new() { Parent = this });

        // Subnets (2 public, 2 private across 2 AZs)
        var azs = Aws.GetAvailabilityZones.Invoke(new() { State = "available" });
        var publicSubnet1 = new Aws.Ec2.Subnet($"{config.SystemKey}-public-1", new()
        {
            VpcId = vpc.Id,
            CidrBlock = "10.0.0.0/18",
            AvailabilityZone = azs.Apply(a => a.Names[0]),
            MapPublicIpOnLaunch = true,
        }, new() { Parent = this });
        // ... publicSubnet2, privateSubnet1, privateSubnet2

        // Internet Gateway
        var igw = new Aws.Ec2.InternetGateway(...);

        // NAT Gateway (single, cost optimization)
        var eip = new Aws.Ec2.Eip(...);
        var natGw = new Aws.Ec2.NatGateway($"{config.SystemKey}-nat", new()
        {
            SubnetId = publicSubnet1.Id,
            AllocationId = eip.Id,
        }, new() { Parent = this });

        // Route tables
        var publicRt = new Aws.Ec2.RouteTable(...);
        new Aws.Ec2.Route("public-route", new()
        {
            RouteTableId = publicRt.Id,
            DestinationCidrBlock = "0.0.0.0/0",
            GatewayId = igw.Id,
        });
        // ... private route table with NAT

        // Security groups (public ALB, internal ALB, ECS public, ECS private, RDS, EFS)
        var albSg = CreateAlbSecurityGroup(config, vpc);
        var internalAlbSg = CreateInternalAlbSecurityGroup(config, vpc);
        var ecsPublicSg = CreateEcsPublicSecurityGroup(config, vpc, albSg, internalAlbSg);
        var ecsPrivateSg = CreateEcsPrivateSecurityGroup(config, vpc, internalAlbSg);
        var rdsSg = CreateRdsSecurityGroup(config, vpc, ecsPublicSg, ecsPrivateSg);
        var efsSg = CreateEfsSecurityGroup(config, vpc, ecsPublicSg, ecsPrivateSg);

        // Public and private ALBs
        var publicAlb = new Aws.LB.LoadBalancer($"{config.SystemKey}-public-alb", new()
        {
            Internal = false,
            LoadBalancerType = "application",
            SecurityGroups = { albSg.Id },
            Subnets = { publicSubnet1.Id, publicSubnet2.Id },
        }, new() { Parent = this });

        var internalAlb = new Aws.LB.LoadBalancer($"{config.SystemKey}-internal-alb", new()
        {
            Internal = true,
            SecurityGroups = { internalAlbSg.Id },
            Subnets = { privateSubnet1.Id, privateSubnet2.Id },
        }, new() { Parent = this });

        // HTTPS listeners (require ACM cert)
        var cert = new Aws.Acm.Certificate($"{config.SystemKey}-cert", new()
        {
            DomainName = config.SystemDomain,
            SubjectAlternativeNames = {
                $"*.{config.SystemDomain}",
                $"*.shop.{config.SystemDomain}",
            },
            ValidationMethod = "DNS",
        }, new() { Parent = this });
        // ... DNS validation records, listener creation

        // DNS zones
        var publicZone = ...; // may reference existing zone
        var privateZone = new Aws.Route53.Zone($"{config.SystemKey}-private", new()
        {
            Name = config.SystemDomain,
            Vpcs = { new() { VpcId = vpc.Id } },
        }, new() { Parent = this });

        // Cloud Map namespace
        var namespace_ = new Aws.ServiceDiscovery.PrivateDnsNamespace(...);

        // VPC Flow Logs
        var flowLogGroup = new Aws.CloudWatch.LogGroup(...);
        var flowLog = new Aws.Ec2.FlowLog(...);

        return new AwsNetworkOutputs
        {
            NetworkId = vpc.Id,
            PrivateSubnetIds = Output.All(privateSubnet1.Id, privateSubnet2.Id),
            PublicSubnetIds = Output.All(publicSubnet1.Id, publicSubnet2.Id),
            PublicDnsZoneId = publicZone.Id,
            PrivateDnsZoneId = privateZone.Id,
            // Extended outputs (AWS-specific, cast where needed)
            PublicAlbArn = publicAlb.Arn,
            InternalAlbArn = internalAlb.Arn,
            PublicAlbDns = publicAlb.DnsName,
            InternalAlbDns = internalAlb.DnsName,
            HttpsListenerArn = httpsListener.Arn,
            InternalHttpsListenerArn = internalHttpsListener.Arn,
            EcsPublicSecurityGroupId = ecsPublicSg.Id,
            EcsPrivateSecurityGroupId = ecsPrivateSg.Id,
            RdsSecurityGroupId = rdsSg.Id,
            EfsSecurityGroupId = efsSg.Id,
            EcsClusterId = ..., // from AwsEcsClusterComponent
            CloudMapNamespaceId = namespace_.Id,
            CertificateArn = cert.Arn,
        };
    }
}
```

### AWS-Specific Extended Outputs

The base interfaces define cloud-agnostic outputs. AWS components return extended output types with additional AWS-specific properties:

```csharp
// Cloud-agnostic (in Lz.Core)
public interface INetworkOutputs
{
    Output<string> NetworkId { get; }
    Output<ImmutableArray<string>> PrivateSubnetIds { get; }
    Output<ImmutableArray<string>> PublicSubnetIds { get; }
    Output<string> PrivateDnsZoneId { get; }
    Output<string> PublicDnsZoneId { get; }
}

// AWS-specific (in Lz.Aws)
public class AwsNetworkOutputs : INetworkOutputs
{
    // INetworkOutputs implementation
    public Output<string> NetworkId { get; init; }
    public Output<ImmutableArray<string>> PrivateSubnetIds { get; init; }
    public Output<ImmutableArray<string>> PublicSubnetIds { get; init; }
    public Output<string> PrivateDnsZoneId { get; init; }
    public Output<string> PublicDnsZoneId { get; init; }

    // AWS-specific — used by other AWS components via cast
    public Output<string> PublicAlbArn { get; init; }
    public Output<string> InternalAlbArn { get; init; }
    public Output<string> PublicAlbDns { get; init; }
    public Output<string> InternalAlbDns { get; init; }
    public Output<string> HttpsListenerArn { get; init; }
    public Output<string> InternalHttpsListenerArn { get; init; }
    public Output<string> EcsPublicSecurityGroupId { get; init; }
    public Output<string> EcsPrivateSecurityGroupId { get; init; }
    public Output<string> RdsSecurityGroupId { get; init; }
    public Output<string> EfsSecurityGroupId { get; init; }
    public Output<string> CloudMapNamespaceId { get; init; }
    public Output<string> CertificateArn { get; init; }
}
```

AWS components can safely cast the interface to the concrete type since the factory guarantees all components are from the same platform:

```csharp
public class AwsEcsServiceComponent : IServiceComponent
{
    public IServiceOutputs Deploy(string name, ServiceDefinition def,
        INetworkOutputs network, IComputeEnvironmentOutputs compute, ...)
    {
        var awsNetwork = (AwsNetworkOutputs)network;

        var targetGroup = new Aws.LB.TargetGroup(..., new()
        {
            VpcId = awsNetwork.NetworkId,
        });

        var listenerRule = new Aws.LB.ListenerRule(..., new()
        {
            ListenerArn = def.IngressType == IngressType.Internal
                ? awsNetwork.InternalHttpsListenerArn
                : awsNetwork.HttpsListenerArn,
        });
        // ...
    }
}
```

## CLI Tool (Lz.Cli)

### Overview

The `lz` command is a dotnet global tool. System-specific behavior is loaded at runtime via the plugin architecture — the tool itself contains no system-specific code.

### Installation

```bash
cd Lzm
dotnet pack Lz.Cli -o ./nupkg
dotnet tool install -g Lz.Cli --add-source ./nupkg
```

### Config Resolution Logic

All commands resolve `env`, `systemkey`, and `tenantkey` automatically:

**Environment** (`--env` or folder hierarchy):
1. If `--env` is specified, use that value
2. Otherwise, walk upward from cwd: `_Dev*` → `dev`, `_Test*` → `test`, `_Prod*` → `prod`
3. Error if neither found

**SystemKey** (`--systemkey` or file discovery):
1. If `--systemkey` is specified, load `systemconfig.{sk}.{env}.yaml`
2. Otherwise, discover all `systemconfig.*.{env}.yaml` in the directory
3. One match → auto-select. Multiple → deploy all. None → error.

**TenantKey** (`--tenantkey` or file discovery, for `deploytenant`):
1. If `--tenantkey` is specified, deploy just that tenant
2. Otherwise, discover all `tenantconfig.{sk}.*.{env}.yaml`, deploy each

### Plugin Discovery

The tool searches upward from cwd using two mechanisms:

1. **lz.json marker file** — explicit path to the plugin DLL (e.g., `{"plugin": "Deploy/bin/Debug/net9.0/Deploy.dll"}`)
2. **Convention** — looks for `Deploy/bin/{Debug|Release}/net9.0/Deploy.dll`

Convention-based discovery makes `lz.json` optional — just name your plugin project `Deploy/` and it's found automatically.

The DLL is loaded via `Assembly.LoadFrom` with an `AssemblyDependencyResolver` for plugin-specific deps. The first `ILzPlugin` implementation is instantiated.

Core commands (`deployshared`, `deploysystem`, etc.) are built into `Lz.Cli`. Plugin commands (e.g., `seed`) are registered via `ILzPlugin.RegisterCommands(RootCommand)`.

### Lz.Cli/Program.cs

```csharp
// Plugin loaded at startup (optional — core commands work without one)
ILzPlugin? plugin = PluginLoader.LoadPlugin();

// Shared options reused across commands
var systemKeyOption = new Option<string?>("--systemkey", ...);
var envOption = new Option<string?>("--env", ...);

// Core commands
RegisterDeploySharedCommand(rootCommand);
RegisterDeployFoundationCommand(rootCommand, plugin, systemKeyOption, envOption);
RegisterDeployTenantCommand(rootCommand, plugin, systemKeyOption, envOption);
RegisterDestroyCommand(rootCommand, plugin, systemKeyOption, envOption);
RegisterStatusCommand(rootCommand, plugin, systemKeyOption, envOption);

// Plugin-specific commands (e.g., seed export/import/list)
plugin?.RegisterCommands(rootCommand);
```

Each command handler follows the same pattern:

```csharp
// deploysystem handler
cmd.SetHandler(async (systemKey, env, platform, topology) =>
{
    RequirePlugin(plugin, "deploysystem");
    var resolvedEnv = ConfigResolver.ResolveEnvironment(env);
    var configs = ConfigResolver.ResolveSystemConfigs(resolvedEnv, systemKey);

    foreach (var config in configs)
    {
        var (system, factory) = PrepareSystem(plugin!, config);
        var deployment = new SystemDeployment(factory, system, config);
        await deployment.DeployFoundationAsync();
    }
}, systemKeyOption, envOption, platformOption, topologyOption);
```

## State Bootstrap

Each account needs a state bucket and encryption key before the first deployment. This is a one-time setup, deliberately kept outside Pulumi (bootstrap problem).

**Current implementation:** `AwsStateBootstrapper.BootstrapAsync()` in `Lz.Aws` handles this automatically. It is called at the start of `deployshared` and `deploysystem` (in `Program.cs`) before the Pulumi orchestrator runs. The bootstrapper is idempotent — it creates the S3 bucket and KMS key only if they don't already exist. No separate `lz bootstrap` command is needed.

The bootstrapper reads `StateConfig.Backend` and `StateConfig.SecretsProvider` which are auto-generated by `ConfigLoader` from `SystemSuffix`/`SharedSuffix`. The `State:` section is no longer specified in YAML — it's computed at load time with the suffix at the end of the resource name (e.g., `s3://med-dev-pulumi-state-4498-a704?region=us-west-2`).

### AWS Bootstrap Script (manual alternative)

```bash
#!/bin/bash
# bootstrap-state.sh — run once per AWS account
set -e

SYSTEM_KEY=$1    # e.g., "med"
ENVIRONMENT=$2   # e.g., "dev"
REGION=$3        # e.g., "us-east-1"
PROFILE=$4       # e.g., "med-dev"

BUCKET_NAME="${SYSTEM_KEY}-${ENVIRONMENT}-pulumi-state"
KEY_ALIAS="alias/${SYSTEM_KEY}-${ENVIRONMENT}-pulumi-key"

# Create KMS key for state encryption
KEY_ID=$(aws kms create-key \
    --description "Pulumi state encryption for ${SYSTEM_KEY} ${ENVIRONMENT}" \
    --profile $PROFILE --region $REGION \
    --query 'KeyMetadata.KeyId' --output text)

aws kms create-alias \
    --alias-name $KEY_ALIAS \
    --target-key-id $KEY_ID \
    --profile $PROFILE --region $REGION

# Create S3 bucket with versioning and encryption
aws s3api create-bucket \
    --bucket $BUCKET_NAME \
    --region $REGION \
    --profile $PROFILE \
    --create-bucket-configuration LocationConstraint=$REGION

aws s3api put-bucket-versioning \
    --bucket $BUCKET_NAME \
    --versioning-configuration Status=Enabled \
    --profile $PROFILE --region $REGION

aws s3api put-bucket-encryption \
    --bucket $BUCKET_NAME \
    --server-side-encryption-configuration '{
        "Rules": [{"ApplyServerSideEncryptionByDefault": {
            "SSEAlgorithm": "aws:kms",
            "KMSMasterKeyID": "'$KEY_ID'"
        }}]
    }' \
    --profile $PROFILE --region $REGION

aws s3api put-public-access-block \
    --bucket $BUCKET_NAME \
    --public-access-block-configuration \
        BlockPublicAcls=true,IgnorePublicAcls=true,BlockPublicPolicy=true,RestrictPublicBuckets=true \
    --profile $PROFILE --region $REGION

# Create DynamoDB table for locking
aws dynamodb create-table \
    --table-name "${SYSTEM_KEY}-${ENVIRONMENT}-pulumi-lock" \
    --attribute-definitions AttributeName=LockID,AttributeType=S \
    --key-schema AttributeName=LockID,KeyType=HASH \
    --billing-mode PAY_PER_REQUEST \
    --profile $PROFILE --region $REGION

echo ""
echo "State infrastructure created."
echo ""
echo "Note: State.Backend and State.SecretsProvider are auto-generated"
echo "from SystemSuffix in systemconfig — no manual YAML entry needed."
```

### Azure Bootstrap Script

```bash
#!/bin/bash
# bootstrap-state-azure.sh — run once per Azure subscription
set -e

SYSTEM_KEY=$1
ENVIRONMENT=$2
LOCATION=$3        # e.g., "eastus"
RESOURCE_GROUP="${SYSTEM_KEY}-${ENVIRONMENT}-state"
STORAGE_ACCOUNT="${SYSTEM_KEY}${ENVIRONMENT}state"  # must be globally unique, lowercase
CONTAINER_NAME="pulumistate"
VAULT_NAME="${SYSTEM_KEY}-${ENVIRONMENT}-vault"

# Create resource group
az group create --name $RESOURCE_GROUP --location $LOCATION

# Create storage account
az storage account create \
    --name $STORAGE_ACCOUNT \
    --resource-group $RESOURCE_GROUP \
    --location $LOCATION \
    --sku Standard_LRS \
    --encryption-services blob

# Create blob container
az storage container create \
    --name $CONTAINER_NAME \
    --account-name $STORAGE_ACCOUNT

# Create Key Vault with encryption key
az keyvault create \
    --name $VAULT_NAME \
    --resource-group $RESOURCE_GROUP \
    --location $LOCATION

az keyvault key create \
    --vault-name $VAULT_NAME \
    --name pulumi-key \
    --kty RSA \
    --size 2048

echo ""
echo "State infrastructure created."
echo ""
echo "Add to systemconfig.${SYSTEM_KEY}.${ENVIRONMENT}.yaml:"
echo ""
echo "State:"
echo "  Backend: azblob://${CONTAINER_NAME}"
echo "  SecretsProvider: azurekeyvault://${VAULT_NAME}.vault.azure.net/keys/pulumi"
echo ""
echo "Set environment variables:"
echo "  AZURE_STORAGE_ACCOUNT=${STORAGE_ACCOUNT}"
echo "  AZURE_STORAGE_KEY=<from portal or az storage account keys list>"
```

## Migration Strategy (CloudFormation to Pulumi)

### Step 1: Deploy Pulumi Alongside CloudFormation (Dev)

Run both systems in parallel in dev. Pulumi manages new resources; CloudFormation manages existing ones. Validate that Pulumi creates equivalent resources.

### Step 2: Import Existing Resources (Dev)

```bash
# Export resource IDs from CloudFormation
aws cloudformation describe-stack-resources \
    --stack-name {systemkey}---system \
    --query 'StackResources[].{Type:ResourceType,Id:PhysicalResourceId,Logical:LogicalResourceId}'

# Import into Pulumi state
pulumi import aws:ec2/vpc:Vpc system-vpc vpc-0a1b2c3d4e5f
pulumi import aws:rds/instance:Instance postgres {systemkey}-db-instance
# ... for each resource

# Verify: preview should show no changes
pulumi preview
# Expected output: "0 to create, 0 to update, 0 to delete"
```

### Step 3: Delete CloudFormation Stacks (Dev)

```bash
# Retain all resources (don't actually delete them)
aws cloudformation delete-stack \
    --stack-name {systemkey}---system \
    --retain-resources VPC Subnet1 Subnet2 ... # list all logical IDs

# Resources are now managed exclusively by Pulumi
```

### Step 4: Repeat for Test, Then Prod

Same process, one environment at a time, with validation between each.

## Testing Strategy

### Unit Tests (Lz.Tests)

```csharp
// Test orchestration with mocked components
[Fact]
public async Task Deploy_Foundation_Creates_Network_Database_FileStorage_Compute_Auth()
{
    var mockFactory = new Mock<IPlatformFactory>();
    var mockNetwork = new Mock<ISystemNetworkComponent>();
    var mockDb = new Mock<IDatabaseComponent>();
    // ...

    mockFactory.Setup(f => f.CreateNetwork()).Returns(mockNetwork.Object);
    mockFactory.Setup(f => f.CreateDatabase()).Returns(mockDb.Object);
    // ...

    var config = TestConfig.Create(platform: "aws", topology: "ecs-fargate-keycloak");
    var system = new TestSystem();
    var deployment = new SystemDeployment(mockFactory.Object, system, config);

    await deployment.RunAsync("foundation");

    mockNetwork.Verify(n => n.Deploy(config), Times.Once);
    mockDb.Verify(d => d.Deploy(config, It.IsAny<INetworkOutputs>()), Times.Once);
    // ...
}

// Test systemconfig loading — SystemKey and Environment derived from filename
[Fact]
public void ConfigLoader_Parses_SystemKey_And_Environment_From_Filename()
{
    var config = ConfigLoader.LoadSystemConfig("testdata/systemconfig.testapp.dev.yaml");
    Assert.Equal("testapp", config.SystemKey);     // derived from filename
    Assert.Equal("dev", config.Environment);       // derived from filename
    Assert.Equal("aws", config.Platform);
    Assert.Equal("ecs", config.Topology);
    Assert.NotNull(config.ECS);
    // Note: Tenants are NOT in systemconfig — discovered from tenantconfig files on disk
}

// Test tenantconfig loading — SystemKey, TenantKey, Environment from filename
// TenantConfig includes deployment fields: RootDomain, HostedZoneId, etc.
[Fact]
public void ConfigLoader_Parses_TenantConfig_From_Filename()
{
    var tenantConfig = ConfigLoader.LoadTenantConfig("testdata/tenantconfig.testapp.meadows.dev.yaml");
    Assert.Equal("testapp", tenantConfig.SystemKey);    // derived from filename
    Assert.Equal("meadows", tenantConfig.TenantKey);    // derived from filename
    Assert.Equal("dev", tenantConfig.Environment);      // derived from filename
    Assert.Equal("testdev.click", tenantConfig.RootDomain);
    Assert.Equal("testapp/meadows", tenantConfig.SecretsManager.SecretPrefix);
}

// Test config merging — tenant overrides system defaults
[Fact]
public void ConfigMerger_Applies_Tenant_Overrides()
{
    var systemConfig = ConfigLoader.LoadSystemConfig("testdata/systemconfig.testapp.dev.yaml");
    var tenantConfig = ConfigLoader.LoadTenantConfig("testdata/tenantconfig.testapp.mp.dev.yaml");
    var merged = ConfigMerger.MergeRuntime(systemConfig, tenantConfig);
    Assert.Equal("testapp/mp", merged.SecretsManager.SecretPrefix);  // tenant override
}

// Test topology validation
[Fact]
public void Validator_Rejects_Volumes_In_Lambda_Topology()
{
    var system = new SystemDefinition();
    system.AddService("svc", new() { Volumes = { new("data", "/data", "/data") } });

    var result = TopologyValidator.Validate(system, "lambda");

    Assert.False(result.IsValid);
    Assert.Contains("volumes", result.Errors[0], StringComparison.OrdinalIgnoreCase);
}
```

### Integration Tests

Integration tests deploy real resources to a dedicated test account, validate, and tear down:

```csharp
[Fact]
[Trait("Category", "Integration")]
public async Task AwsEcsNetworkComponent_Creates_VPC_And_Subnets()
{
    var config = TestConfig.CreateAws("integration-test");
    var component = new AwsEcsNetworkComponent();

    // Deploy
    var outputs = component.Deploy(config);

    // Validate (via AWS SDK)
    var ec2 = new AmazonEC2Client();
    var vpc = await ec2.DescribeVpcsAsync(new() {
        VpcIds = { await outputs.NetworkId.GetValueAsync() }
    });
    Assert.Single(vpc.Vpcs);
    Assert.Equal("10.0.0.0/16", vpc.Vpcs[0].CidrBlock);

    // Tear down happens via Pulumi destroy in test fixture
}
```

## Sizing Map

The `Sizing` config value maps to concrete resource sizes per platform and topology:

```csharp
public static class SizingMap
{
    public static (int Cpu, int Memory) GetContainerSizing(string sizing, string serviceName)
    {
        return (sizing, serviceName) switch
        {
            ("small", "smartstore") => (512, 1024),
            ("small", "apphost")    => (256, 512),
            ("small", "keycloak")   => (512, 1024),
            ("medium", "smartstore") => (1024, 2048),
            ("medium", "apphost")    => (512, 1024),
            ("medium", "keycloak")   => (1024, 2048),
            ("large", "smartstore")  => (2048, 4096),
            ("large", "apphost")     => (1024, 2048),
            ("large", "keycloak")    => (2048, 4096),
            _ => (256, 512),
        };
    }

    public static string GetDbInstanceClass(string sizing, string platform)
    {
        return (sizing, platform) switch
        {
            ("small", "aws")    => "db.t4g.micro",
            ("medium", "aws")   => "db.t4g.small",
            ("large", "aws")    => "db.t4g.medium",
            ("small", "azure")  => "B_Standard_B1ms",
            ("medium", "azure") => "GP_Standard_D2s_v3",
            ("large", "azure")  => "GP_Standard_D4s_v3",
            _ => throw new ArgumentException($"Unknown sizing: {sizing}/{platform}"),
        };
    }
}
```

## Open Questions

1. **Pulumi CLI dependency:** The Automation API still requires the Pulumi engine binary. Investigate whether it auto-downloads or needs bundling.

2. **Provider plugin management:** Pulumi provider plugins (aws, azure) download on first use. Consider pre-bundling or documenting the first-run experience.

3. **Partial deployment within a stack:** Can we use `pulumi up --target` via the Automation API to deploy specific resources within a stack, or do we need separate stacks for every pause point?

4. **CloudFront KVS chunking:** The current LzAws module handles KVS entry chunking for the 1024-byte limit. Determine if this logic moves to Lz.Aws or becomes unnecessary with a different CDN routing approach.

5. **Docker build integration:** Current `Deploy-DockerAws` builds and pushes container images. Determine whether `lz deploycontainer` handles this or if it remains a separate CI step.

6. **Keycloak realm configuration:** Current `kc-upload.ps1` and `kc-report.ps1` manage Keycloak realms. Determine whether this becomes `lz keycloak configure` or remains external.
