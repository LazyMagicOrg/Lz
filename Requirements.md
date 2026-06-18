# Lz Tool — Requirements

## Overview

Lz is a unified .NET tool for development and deployment support of LazyMagic-based systems. It consolidates the current PowerShell-based deployment orchestration (LzAws) and C#-based code generation (LazyMagicMDD) into a single, extensible platform that supports multiple cloud providers and deployment topologies.

## Background

### Current Tooling

1. **LzAws** — PowerShell module (34 public cmdlets, 32 private helpers) that orchestrates AWS deployments via SAM/CloudFormation. Manages multi-tenant SaaS infrastructure including VPC, ALB, ECS, RDS, EFS, CloudFront, Keycloak, SES, and Tailscale.

2. **LazyMagicMDD** — C# solution providing model-driven code generation from OpenAPI specifications and LazyMagic.yaml directives. Generates DTOs, DynamoDB repositories, REST controllers, ASP.NET Core hosts, Dockerfiles, client SDKs, and SAM templates. Ships as a .NET global tool.

3. **Per-system AWS templates** — SAM/CloudFormation templates (e.g., MyApp/Service/AWSTemplates) deployed by LzAws. Typically 6-11 stacks per environment with ~145+ AWS resources.

### Pain Points Addressed

- **Two tools, two languages** — PowerShell for deployment, C# for code generation. Developers must be proficient in both.
- **AWS-only** — Current tooling is tightly coupled to AWS. No path to Azure or GCP.
- **CloudFormation limitations** — Large stacks are slow to deploy, failures trigger full rollbacks, 500-resource limit forces artificial stack splitting.
- **Manual deployment steps** — The deployment process requires human intervention between phases (Tailscale setup, Keycloak realm configuration).
- **Cross-platform fragility** — PowerShell module management and AWS.Tools dependencies are fragile across environments.

## Functional Requirements

### FR-1: Multi-Cloud Platform Support

- **FR-1.1:** Support AWS as the primary deployment target.
- **FR-1.2:** Support Azure as a secondary deployment target.
- **FR-1.3:** Architecture must allow adding GCP or other cloud providers without modifying core orchestration or system definition code.
- **FR-1.4:** The same system definition must be deployable to different cloud platforms without modification.

### FR-2: Multiple Deployment Topologies

- **FR-2.1:** Support ECS + ALB topology on AWS (container-based, long-running services).
- **FR-2.2:** Support Lambda + API Gateway topology on AWS (serverless, per-invocation scaling).
- **FR-2.3:** Support Azure Container Apps topology.
- **FR-2.4:** Topology selection must be a configuration parameter, not a code change.
- **FR-2.5:** Mixed topologies must be supported where required (e.g., Keycloak always runs as a container even when application services run as Lambda functions).

### FR-3: Infrastructure as Code with Pulumi

- **FR-3.1:** Use Pulumi as the IaC engine, replacing SAM/CloudFormation.
- **FR-3.2:** Use the Pulumi Automation API to embed deployment within the Lz CLI — no requirement for users to install or interact with the Pulumi CLI directly.
- **FR-3.3:** Infrastructure resources must be created via direct cloud API calls (Pulumi's model), not via CloudFormation stacks.
- **FR-3.4:** Independent resources must be deployed in parallel where possible.
- **FR-3.5:** Deployments must be incremental — only changed resources are updated.
- **FR-3.6:** Failed deployments must not trigger full rollbacks; successfully created resources must remain in place.

### FR-4: State Management

- **FR-4.1:** Each AWS account (dev, test, prod) must have its own isolated state storage (S3 bucket).
- **FR-4.2:** Each Azure subscription must have its own isolated state storage (Azure Blob container).
- **FR-4.3:** State backend configuration must be specified in the system configuration YAML.
- **FR-4.4:** Secrets in state files must be encrypted using cloud-native KMS (AWS KMS, Azure Key Vault).
- **FR-4.5:** Concurrent deployments to the same stack must be prevented via locking (DynamoDB for S3 backend, blob leases for Azure Blob).
- **FR-4.6:** State must support inspection (`lz state export`), drift detection (`lz refresh`), and resource import (`lz import`).

### FR-5: Phased Deployment with Manual Steps

- **FR-5.1:** Deployments must support multiple phases to accommodate manual intervention steps.
- **FR-5.2:** The tool must validate prerequisites before starting each phase (e.g., check that Tailscale auth key exists in Secrets Manager before deploying services).
- **FR-5.3:** Deployments must be idempotent and progressive — running `lz deploy` again after completing a manual step must pick up where it left off without redeploying existing resources.
- **FR-5.4:** The tool must clearly communicate what manual steps are required and what command to run next.
- **FR-5.5:** Phases must be definable per topology (ECS topology may require different phases than Lambda topology).

### FR-6: Authentication and Credentials

- **FR-6.1:** Support AWS SSO profiles for authentication, consistent with current workflow.
- **FR-6.2:** Support Azure CLI / Entra ID authentication.
- **FR-6.3:** Validate credentials before starting a deployment (pre-flight check for SSO token expiry).
- **FR-6.4:** Support CI/CD environments where SSO is not available (IAM roles, OIDC federation, environment variables).
- **FR-6.5:** Multi-region deployments (e.g., CloudFront certificates in us-east-1) must use the same SSO profile with a different region parameter.

### FR-7: Code Generation Integration

- **FR-7.1:** Retain all current LazyMagicMDD code generation capabilities (DTOs, repositories, controllers, ASP.NET hosts, Dockerfiles, client SDKs).
- **FR-7.2:** LazyMagicMDD must be able to generate system definition code (e.g., `MyAppSystem.g.cs`) from LazyMagic.yaml directives, including service definitions, API routes, and container configurations.
- **FR-7.3:** Generated system definition code must use partial classes to allow hand-authored extensions.
- **FR-7.4:** Code generation and deployment must be invocable from the same CLI tool.

### FR-8: Configuration Model

The current system uses two separate configuration files that serve dual purposes — they drive both infrastructure deployment and runtime application behavior. This separation must be preserved.

#### FR-8.1: Dual-File Configuration

- **FR-8.1.1:** System configuration must use `systemconfig.{systemkey}.{env}.yaml`, preserving the current naming convention. SystemKey and Environment are derived from the filename and must NOT appear as fields inside the file.
- **FR-8.1.2:** Tenant configuration must use `tenantconfig.{systemkey}.{tenantkey}.{env}.yaml`, preserving the current naming convention. SystemKey, TenantKey, and Environment are derived from the filename.
- **FR-8.1.3:** Configuration files must be discoverable via directory traversal (search up from current directory), consistent with current LzAws behavior.
- **FR-8.1.4:** Config files must be located immediately above the Service solution folder, consistent with current convention.

#### FR-8.2: SystemConfig Content

The system configuration file defines system-wide settings used by both deployment and running applications:

**Deployment settings (consumed by Lz tool):**
- System identity: `SystemSuffix` (S3 bucket uniqueness)
- Cloud credentials: `Profile`, `Region`
- System domain: `SystemDomain`, `DefaultTenant`
- Central auth: `CentralAuthDomain` (shared Keycloak domain)
- Infrastructure sizing: `ECS` section (database class, Keycloak image tag, Tailscale config, service CPU/memory)
- CDN settings: `CDN` section (price class, default root object)
- Behaviors: System-level routing rules (APIs, Assets, WebApps) that may be overridden per-tenant or per-subtenant
- State backend: `State` section (Pulumi backend URL, secrets provider)
- Platform/topology: `Platform`, `Topology`

**Runtime application settings (consumed by running containers):**
- Admin identity: `AdminAuth`, `AdminEmail`
- Secrets Manager: `SecretsManager` (SecretPrefix, VerboseLogging)
- Integrations: `Integrations.Services` (service endpoints, deployment type, modules)
- Authentication: `AuthConfigs` (realm URLs, client IDs, audience validation)
- Request handling: `RequestRewriter`, `RequestLogging`, `Authentication`, module-level logging

#### FR-8.3: TenantConfig Content

The tenant configuration file defines per-tenant settings, often inheriting or overriding system-level values:

**Deployment settings (consumed by Lz tool):**
- Tenant domain: `RootDomain`, `HostedZoneId`, `AcmCertificateArn`
- Tenant identity: `TenantSuffix` (S3 bucket uniqueness)
- Cloud credentials: `Profile`, `Region` (can override system defaults)
- Per-tenant infrastructure: EFS paths, database name, ALB listener priorities, service discovery names
- Per-tenant services: CPU/memory sizing, desired count
- CDN settings: can override system defaults
- Behaviors: tenant-level behavior overrides (optional)
- Subtenants: subtenant-specific overrides with SubDomain and Behaviors (optional)

**Runtime application settings (consumed by running containers):**
- Secrets Manager: `SecretsManager` with tenant-scoped `SecretPrefix` (`{SystemKey}/{TenantKey}`)
- Integrations: can override system service endpoints
- Authentication: can override system auth realm URLs
- Request handling: can override system rewrite rules and logging

#### FR-8.4: Configuration Principles

- **FR-8.4.1:** YAML must define environment-varying and tenant-varying values only. System topology (which services exist, how they connect) must be defined in C#.
- **FR-8.4.2:** Tenant configs may override any system-level setting. When a tenant config does not specify a value, the system config value applies as the default.
- **FR-8.4.3:** Subtenant configuration is defined within the tenant config's `Subtenants` section and may override tenant-level behaviors.
- **FR-8.4.4:** The Behaviors hierarchy (System → Tenant → Subtenant) with path-based overrides must be preserved.
- **FR-8.4.5:** No credentials or secrets in YAML configuration files.
- **FR-8.4.6:** The Lz tool must be able to resolve the effective configuration for any tenant by merging system defaults with tenant overrides.

#### FR-8.5: Dual-Purpose Config Delivery

- **FR-8.5.1:** The Lz tool must read both systemconfig and tenantconfig files during deployment to parameterize infrastructure resources.
- **FR-8.5.2:** The Lz tool must deploy the runtime portions of these config files to the appropriate storage (EFS, container environment, config maps) so running applications can read them at startup.
- **FR-8.5.3:** Runtime config sections must remain in the same YAML format currently consumed by the .NET applications — no schema changes to avoid breaking application config binding.

### FR-9: Multi-Tenant Support

- **FR-9.1:** Each tenant must receive dedicated compute resources — per-tenant SmartStore and AppHost ECS services (or equivalent per topology), not shared containers. Tenants do not share application containers.
- **FR-9.2:** Per-tenant services must have isolated configuration: unique ALB listener rule priorities, unique Cloud Map service discovery names, dedicated IAM roles, dedicated EFS access points, and tenant-scoped secrets.
- **FR-9.3:** Support per-tenant infrastructure beyond compute: CDN distribution, S3 storage, DNS records, per-tenant database, per-tenant SES email identity, and per-tenant auth domain routing.
- **FR-9.4:** Support subtenant infrastructure within tenants, including subtenant-specific behavior overrides.
- **FR-9.5:** Tenant operations must be independent — adding, updating, or removing a tenant (including its dedicated services) must not affect other tenants or system-level infrastructure.
- **FR-9.6:** Each tenant must have its own `tenantconfig.{systemkey}.{tenantkey}.{env}.yaml` file specifying deployment isolation settings (listener priorities, service discovery names, EFS paths, database name) and runtime configuration overrides.
- **FR-9.7:** Tenant deployment data (RootDomain, HostedZoneId, AcmCertificateArn, Behaviors, Subtenants) is stored in the tenantconfig file — there is no separate tenant registry in systemconfig.
- **FR-9.8:** Per-tenant resource naming must follow the pattern `{SystemKey}-{TenantKey}-{resource}` to avoid conflicts across tenants.
- **FR-9.9:** The Lz tool must enumerate tenants by discovering `tenantconfig.{systemkey}.*.{env}.yaml` files on disk using the ConfigResolver. No centralized registry in systemconfig.
- **FR-9.10:** System-level infrastructure (VPC, ECS cluster, RDS, EFS, Tailscale) must be deployed separately from per-tenant services, in a different Pulumi stack with a different lifecycle.

### FR-10: CLI Interface

- **FR-10.1:** Ship as a dotnet global tool (`lz`), with system-specific behavior loaded at runtime via an `ILzPlugin` plugin discovered by convention from the `Deploy/` folder (or optionally via `lz.json` marker files).
- **FR-10.2:** Support commands: `deployshared`, `deploysystem`, `deploytenant`, `destroy`, `status`. Plugin-specific commands (e.g., `seed`) are registered by the plugin.
- **FR-10.3:** Support smart defaults via ConfigResolver: environment auto-detected from folder hierarchy (`_Dev*` → dev, `_Test*` → test, `_Prod*` → prod), systemkey auto-detected when only one systemconfig exists.
- **FR-10.4:** Support platform and topology override: `lz deploysystem --platform aws --topology ecs-fargate-keycloak`.
- **FR-10.5:** Support tenant-specific operations: `lz deploytenant --tenantkey meadows`. Without `--tenantkey`, deploy all discovered tenants.
- **FR-10.6:** Zero-flag operation when possible: `lz deploysystem` and `lz deploytenant` must work with no flags when run from a properly structured repo with a single systemconfig.
- **FR-10.7:** Provide clear, actionable output including next steps after each phase (transition gates).

## Non-Functional Requirements

### NFR-1: Reusability

- **NFR-1.1:** Infrastructure components (Lz.Core, Lz.Aws, Lz.Azure) must be published as NuGet packages, reusable across multiple systems.
- **NFR-1.2:** System-specific code (e.g., MyAppSystem.cs) must be confined to per-system projects that reference the shared packages.

### NFR-2: Extensibility

- **NFR-2.1:** Adding a new cloud platform must require only implementing the platform factory and components — no changes to core interfaces or orchestration.
- **NFR-2.2:** Adding a new deployment topology must require only implementing topology-specific components and a factory — no changes to core interfaces or system definitions.

### NFR-3: Testability

- **NFR-3.1:** Orchestration logic must be unit-testable with mocked platform components.
- **NFR-3.2:** Platform components must be integration-testable against real cloud environments.
- **NFR-3.3:** Configuration parsing and validation must be unit-testable.

### NFR-4: Security

- **NFR-4.1:** Secrets in Pulumi state must be encrypted at rest (KMS/Key Vault).
- **NFR-4.2:** Prefer cloud-managed secrets (RDS-managed passwords, external secret references) over Pulumi-generated secrets where possible, to minimize secrets in state.
- **NFR-4.3:** State storage buckets must have versioning enabled and appropriate access policies.
- **NFR-4.4:** No credentials or secrets in YAML configuration files.

### NFR-5: Migration

- **NFR-5.1:** Existing CloudFormation-managed resources must be importable into Pulumi state without downtime or recreation.
- **NFR-5.2:** The migration path must support incremental adoption — not all stacks need to migrate simultaneously.

### NFR-6: Cost

- **NFR-6.1:** No Pulumi SaaS licensing costs — self-hosted state backends only.
- **NFR-6.2:** State infrastructure costs must be minimal (~$1-2/month per environment).
