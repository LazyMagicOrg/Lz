using Lz.Aws.AppRunner;
using Lz.Aws.Config;
using Lz.Aws.Ecs;
using Lz.Aws.EcsExpress;
using Lz.Aws.Lambda;
using Lz.Aws.Interfaces;
using Lz.Core.Config;

namespace Lz.Aws.Topologies;

/// <summary>
/// Registry of the known AWS topologies. Each entry fully specifies what the
/// topology bundles (axes + behavior), so readers don't have to archaeologize
/// a factory body to understand what <c>Topology: ecs-fargate-keycloak</c>
/// means in a systemconfig YAML.
/// </summary>
/// <remarks>
/// The built-in topologies are exposed as <c>static readonly</c> fields
/// (<see cref="EcsFargateKeycloak"/>, <see cref="EcsFargateCognitoDynamodb"/>,
/// <see cref="AppRunner"/>). Plugins contribute additional or overriding
/// topologies via <see cref="ILzPlugin.RegisterTopologies"/>, which calls
/// <see cref="Register"/> and optionally <see cref="DeriveFrom"/>.
/// The registry is single-writer / multi-reader: all <c>Register</c> calls
/// happen once at CLI startup before any <c>CreateFactory</c> invocation.
/// </remarks>
public static class AwsTopologies
{
    /// <summary>
    /// Full private-VPC deployment with centralized Keycloak, RDS, EFS,
    /// and Tailscale VPN. Heaviest topology; suits systems that need a
    /// shared relational DB, POSIX file storage, or VPN-gated admin access.
    /// </summary>
    public static readonly AwsTopology EcsFargateKeycloak = new()
    {
        Name = "ecs-fargate-keycloak",
        Summary = "ECS Fargate in private VPC + Keycloak + RDS + EFS + Tailscale",
        Description = """
            Full-stack long-running topology. Provisions a VPC with public + private
            subnets and NAT, ECS Fargate tasks in the private subnets, RDS
            PostgreSQL, EFS file storage, Tailscale subnet routers for admin VPN
            access, and a gate-checker Lambda for in-VPC readiness checks. Auth is
            delegated to a centralized Keycloak deployed separately via the
            shared-services account (`lz deployshared`).
            """,
        Compute = AwsComputeKind.FargatePrivate,
        Data = AwsDataKind.Rds,
        FileStorage = AwsFileStorageKind.Efs,
        Auth = AwsAuthKind.Keycloak,
        HasPrivateNetwork = true,
        UsesVpn = true,
        UsesCentralAuth = true,
        UsesInVpcGateChecker = true,
        UsesSeedTask = true,
        CreateFactory = config => new AwsEcsPlatformFactory(config),
        ValidateConfig = (cfg, errs) =>
        {
            AwsNamingValidator.ValidateSystemKeys(cfg, errs);
            if (string.IsNullOrEmpty(cfg.VpcCidr))
                errs.Add("VpcCidr is required for the ecs-fargate-keycloak topology (private VPC).");
            if (string.IsNullOrEmpty(cfg.CentralAuthDomain))
                errs.Add("CentralAuthDomain is required for the ecs-fargate-keycloak topology (shared Keycloak).");
        },
    };

    /// <summary>
    /// Lighter-weight container topology with per-environment Cognito instead
    /// of centralized Keycloak, and DynamoDB + S3 instead of RDS + EFS. Fargate
    /// runs in public subnets so no NAT is required.
    /// </summary>
    public static readonly AwsTopology EcsFargateCognitoDynamodb = new()
    {
        Name = "ecs-fargate-cognito-dynamodb",
        Summary = "ECS Fargate in public VPC + Cognito + DynamoDB + S3",
        Description = """
            Slimmer container topology. Provisions a VPC with public subnets only
            (no NAT gateway), ECS Fargate tasks with public IPs, per-environment
            Cognito user pools, DynamoDB for data, and S3 for tenant assets. Good
            fit for systems that want containers but don't need a shared filesystem,
            relational DB, or centralized Keycloak.
            """,
        Compute = AwsComputeKind.FargatePublic,
        Data = AwsDataKind.DynamoDb,
        FileStorage = AwsFileStorageKind.S3,
        Auth = AwsAuthKind.Cognito,
        HasPrivateNetwork = true,
        UsesVpn = false,
        UsesCentralAuth = false,
        UsesInVpcGateChecker = false,
        UsesSeedTask = false,
        CreateFactory = config => new AwsEcsExpressPlatformFactory(config),
        ValidateConfig = (cfg, errs) =>
        {
            AwsNamingValidator.ValidateSystemKeys(cfg, errs);
            if (string.IsNullOrEmpty(cfg.VpcCidr))
                errs.Add("VpcCidr is required for the ecs-fargate-cognito-dynamodb topology.");
            RequireAuthConfigs(cfg, errs, "ecs-fargate-cognito-dynamodb");
        },
    };

    /// <summary>
    /// Fully serverless topology — AppRunner + Cognito + DynamoDB. No VPC,
    /// no NAT, no shared filesystem. Scales to zero; cold-start cost applies.
    /// </summary>
    public static readonly AwsTopology AppRunner = new()
    {
        Name = "apprunner",
        Summary = "AWS AppRunner + Cognito + DynamoDB + S3",
        Description = """
            Serverless-container topology. AppRunner hosts the application,
            scaling request-driven. Cognito for auth, DynamoDB for data, S3 for
            tenant assets. No VPC, no NAT, no shared filesystem. Simplest to
            operate; suits smaller systems without long-running workloads.
            """,
        Compute = AwsComputeKind.AppRunner,
        Data = AwsDataKind.DynamoDb,
        FileStorage = AwsFileStorageKind.S3,
        Auth = AwsAuthKind.Cognito,
        HasPrivateNetwork = false,
        UsesVpn = false,
        UsesCentralAuth = false,
        UsesInVpcGateChecker = false,
        UsesSeedTask = false,
        CreateFactory = config => new AwsAppRunnerPlatformFactory(config),
        ValidateConfig = (cfg, errs) =>
        {
            AwsNamingValidator.ValidateSystemKeys(cfg, errs);
            RequireAuthConfigs(cfg, errs, "apprunner");
        },
    };

    /// <summary>
    /// True-serverless variant of <see cref="EcsFargateCognitoDynamodb"/>: the
    /// per-tenant container Lambda (same image as Fargate) replaces the ECS task,
    /// and a CloudFront-private Function URL (OAC + AWS_IAM) replaces the ALB.
    /// Shares Cognito/DynamoDB/S3 and the CloudFront edge component identities with
    /// the Fargate topology, so a deployed stack can be switched in place.
    /// </summary>
    public static readonly AwsTopology LambdaCognitoDynamodb = new()
    {
        Name = "lambda-cognito-dynamodb",
        Summary = "AWS Lambda (container) + CloudFront Function URL + Cognito + DynamoDB + S3",
        Description = """
            True-serverless variant of ecs-fargate-cognito-dynamodb. The per-tenant
            container Lambda — the SAME ECR image as the Fargate topology, with the
            tenant injected via the TENANT_KEY env var — replaces the ECS task. A
            CloudFront-fronted Lambda Function URL (Origin Access Control + AWS_IAM
            SigV4) replaces the ALB, so the Function URL is private to CloudFront.
            Cognito for auth, DynamoDB for data, S3 for tenant assets. No VPC, no NAT;
            scales to zero. The CloudFront edge, Cognito pools, and DynamoDB tables
            reuse the same component identities as ecs-fargate-cognito-dynamodb, so a
            deployed stack can be switched in place between the two
            (deploysystem + deploytenant). Run `lz deploycontainer` before
            `lz deploytenant` — the function references the {ecr}:latest image.
            See Platform/LambdaTopology.md.
            """,
        Compute = AwsComputeKind.Lambda,
        Data = AwsDataKind.DynamoDb,
        FileStorage = AwsFileStorageKind.S3,
        Auth = AwsAuthKind.Cognito,
        HasPrivateNetwork = false,
        UsesVpn = false,
        UsesCentralAuth = false,
        UsesInVpcGateChecker = false,
        UsesSeedTask = false,
        CreateFactory = config => new AwsLambdaPlatformFactory(config),
        ValidateConfig = (cfg, errs) =>
        {
            AwsNamingValidator.ValidateSystemKeys(cfg, errs);
            RequireAuthConfigs(cfg, errs, "lambda-cognito-dynamodb");
        },
    };

    /// <summary>
    /// Topology-validation helper: require at least one Cognito pool declared
    /// in <c>SystemConfig.AuthConfigs</c>. Used by all topologies where
    /// <c>Auth == <see cref="AwsAuthKind.Cognito"/></c>.
    /// </summary>
    private static void RequireAuthConfigs(SystemConfig cfg, List<string> errs, string topologyName)
    {
        if (cfg.AuthConfigs is null || cfg.AuthConfigs.Count == 0)
        {
            errs.Add(
                $"AuthConfigs is required for the {topologyName} topology — declare at " +
                "least one Cognito pool in systemconfig (e.g. `AuthConfigs:\\n  tenantauth: {{}}`).");
            return;
        }
        AwsAuthValidator.Validate(cfg, errs);
    }

    // Mutable backing store for the registry. Single-writer (plugin startup) /
    // multi-reader (factory resolution) lifecycle — no locking needed given
    // that contract.
    private static readonly Dictionary<string, AwsTopology> _registry =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [EcsFargateKeycloak.Name] = EcsFargateKeycloak,
            [EcsFargateCognitoDynamodb.Name] = EcsFargateCognitoDynamodb,
            [AppRunner.Name] = AppRunner,
            [LambdaCognitoDynamodb.Name] = LambdaCognitoDynamodb,
        };

    /// <summary>
    /// All registered AWS topologies, keyed by <see cref="AwsTopology.Name"/>
    /// (case-insensitive). Includes both built-ins and plugin-contributed
    /// topologies.
    /// </summary>
    public static IReadOnlyDictionary<string, AwsTopology> ByName => _registry;

    /// <summary>
    /// Look up a topology by name. Throws with a helpful message listing the
    /// known topology names if the requested one isn't registered.
    /// </summary>
    public static AwsTopology Get(string name)
    {
        if (_registry.TryGetValue(name, out var topology))
            return topology;
        throw new ArgumentException(
            $"Unknown AWS topology '{name}'. Known topologies: {string.Join(", ", _registry.Keys)}.");
    }

    /// <summary>
    /// Register a new topology or replace an existing one. Plugins call this
    /// from <see cref="ILzPlugin.RegisterTopologies"/> at CLI startup.
    /// </summary>
    /// <param name="topology">The descriptor to register.</param>
    /// <param name="allowOverride">
    /// When false (default), throws if a topology with the same name already
    /// exists. Set true only when a plugin intentionally replaces a built-in
    /// topology's meaning for its own system — prefer <see cref="DeriveFrom"/>
    /// with a new name otherwise.
    /// </param>
    public static void Register(AwsTopology topology, bool allowOverride = false)
    {
        if (topology is null) throw new ArgumentNullException(nameof(topology));
        if (string.IsNullOrWhiteSpace(topology.Name))
            throw new ArgumentException("Topology.Name must not be empty.", nameof(topology));
        if (topology.CreateFactory is null)
            throw new ArgumentException(
                $"Topology '{topology.Name}' must provide a CreateFactory delegate.",
                nameof(topology));

        if (_registry.TryGetValue(topology.Name, out var existing) && !allowOverride)
            throw new InvalidOperationException(
                $"Topology '{topology.Name}' is already registered (as '{existing.Summary}'). " +
                $"Pass allowOverride: true to replace it, or call DeriveFrom with a new name.");

        _registry[topology.Name] = topology;
    }

    /// <summary>
    /// Remove a topology from the registry. Test/debugging hook — plugins
    /// normally don't need this. Returns true if a topology was removed.
    /// </summary>
    public static bool Unregister(string name) => _registry.Remove(name);

    /// <summary>
    /// Produce a new topology descriptor by copying another and replacing
    /// the identity (name/summary/description) plus the factory builder.
    /// Axis flags and <see cref="AwsTopology.ValidateConfig"/> inherit from
    /// the base unless the respective optional parameter is provided.
    /// Convenient for "same shape, different component wiring".
    /// </summary>
    /// <param name="baseTopology">The topology to derive from.</param>
    /// <param name="name">Unique name for the derived topology.</param>
    /// <param name="summary">One-line summary for <c>lz status</c>.</param>
    /// <param name="createFactory">Factory builder for the derived topology (typically a plugin-specific <see cref="IAwsPlatformFactory"/> subclass).</param>
    /// <param name="description">Optional multi-line description; falls back to the base's description.</param>
    /// <param name="validateConfig">Optional additional config validation; falls back to the base's validator.</param>
    public static AwsTopology DeriveFrom(
        AwsTopology baseTopology,
        string name,
        string summary,
        Func<SystemConfig, IAwsPlatformFactory> createFactory,
        string? description = null,
        Action<SystemConfig, List<string>>? validateConfig = null)
    {
        if (baseTopology is null) throw new ArgumentNullException(nameof(baseTopology));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name must not be empty.", nameof(name));
        if (string.IsNullOrWhiteSpace(summary)) throw new ArgumentException("summary must not be empty.", nameof(summary));
        if (createFactory is null) throw new ArgumentNullException(nameof(createFactory));

        return new AwsTopology
        {
            Name = name,
            Summary = summary,
            Description = description ?? baseTopology.Description,
            Compute = baseTopology.Compute,
            Data = baseTopology.Data,
            FileStorage = baseTopology.FileStorage,
            Auth = baseTopology.Auth,
            HasPrivateNetwork = baseTopology.HasPrivateNetwork,
            UsesVpn = baseTopology.UsesVpn,
            UsesCentralAuth = baseTopology.UsesCentralAuth,
            UsesInVpcGateChecker = baseTopology.UsesInVpcGateChecker,
            UsesSeedTask = baseTopology.UsesSeedTask,
            CreateFactory = createFactory,
            ValidateConfig = validateConfig ?? baseTopology.ValidateConfig,
        };
    }
}
