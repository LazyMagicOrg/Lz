using Lz.Core.Config;
using Lz.Core.Interfaces;
using Lz.Core.Interfaces.Outputs;
using AuthPoolDetail = Lz.Core.Interfaces.Outputs.AuthPoolDetail;
using Pulumi;
using Pulumi.Aws;
using Pulumi.Aws.Acm;
using Pulumi.Aws.Cognito;
using Pulumi.Aws.Cognito.Inputs;
using Pulumi.Aws.CloudWatch;

namespace Lz.Aws.AppRunner;

/// <summary>
/// Cognito auth component with custom domains, device tracking, and Hosted UI support.
/// Creates user pools, clients, identity pools, custom domains, and ACM certs.
/// Custom domain mapping:
///   tenantauth  → auth.{systemDomain}
///   plannerauth → auth-planner.{systemDomain}
///   systemauth  → auth-system.{systemDomain}
/// </summary>
public class AwsAppRunnerCognitoComponent : ComponentResource, IAuthServiceComponent
{
    // Maps auth type to custom domain prefix
    private static readonly Dictionary<string, string> DomainPrefixMap = new()
    {
        ["tenantauth"] = "auth",
        ["plannerauth"] = "auth-planner",
        ["systemauth"] = "auth-system",
    };

    public AwsAppRunnerCognitoComponent()
        : base("lz:aws:Cognito", "auth", ResourceArgs.Empty, null)
    {
    }

    public IServiceOutputs Deploy(
        SystemConfig config,
        INetworkOutputs network,
        IComputeEnvironmentOutputs compute,
        IDatabaseOutputs database,
        IFileStorageOutputs fileStorage,
        bool enableAdminBlocking)
    {
        var sk = config.SystemKey;
        var env = config.Environment;
        var suffix = config.SystemSuffix;
        var region = config.Region;
        var systemDomain = config.SystemDomain;
        var prefix = $"{sk}-{env}";

        var authTypes = config.AuthConfigs?.Keys.ToList()
            ?? new List<string> { "tenantauth", "plannerauth", "systemauth" };

        // us-east-1 provider for ACM certs (Cognito custom domains use CloudFront internally)
        var usEast1 = new Provider($"{prefix}-cognito-us-east-1", new ProviderArgs
        {
            Region = "us-east-1",
        }, new CustomResourceOptions { Parent = this });

        // Route 53 zone for DNS validation and alias records
        var publicZone = Pulumi.Aws.Route53.GetZone.Invoke(
            new Pulumi.Aws.Route53.GetZoneInvokeArgs { Name = systemDomain });

        var poolOutputs = new Dictionary<string, CognitoPoolOutputs>();

        foreach (var authType in authTypes)
        {
            var poolPrefix = $"{prefix}-{authType}";
            var domainPrefix = DomainPrefixMap.GetValueOrDefault(authType, $"auth-{authType}");
            var customDomain = $"{domainPrefix}.{systemDomain}";

            // =================================================================
            // USER POOL — with device tracking (user opt-in for "remember me")
            // =================================================================

            var userPool = new UserPool($"{poolPrefix}-pool", new UserPoolArgs
            {
                Name = $"{sk}-{suffix}-{env}-{authType}",
                AutoVerifiedAttributes = { "email" },
                UsernameAttributes = { "email" },
                MfaConfiguration = "OFF",
                AdminCreateUserConfig = new UserPoolAdminCreateUserConfigArgs
                {
                    AllowAdminCreateUserOnly = true,
                },
                DeviceConfiguration = new UserPoolDeviceConfigurationArgs
                {
                    ChallengeRequiredOnNewDevice = true,
                    DeviceOnlyRememberedOnUserPrompt = true, // "Remember this device?" prompt
                },
                PasswordPolicy = new UserPoolPasswordPolicyArgs
                {
                    MinimumLength = 8,
                    RequireLowercase = true,
                    RequireUppercase = true,
                    RequireNumbers = true,
                    RequireSymbols = false,
                },
                AccountRecoverySetting = new UserPoolAccountRecoverySettingArgs
                {
                    RecoveryMechanisms =
                    {
                        new UserPoolAccountRecoverySettingRecoveryMechanismArgs
                        {
                            Name = "verified_email",
                            Priority = 1,
                        },
                    },
                },
                Tags =
                {
                    { "System", sk },
                    { "AuthType", authType },
                    { "ManagedBy", "lz-pulumi" },
                },
            }, new CustomResourceOptions { Parent = this });

            // =================================================================
            // USER POOL CLIENT — with OAuth/Hosted UI configuration
            // =================================================================

            // Callback URL on the tenant's root domain — CFAuthCallback.js redirects to subtenant
            var callbackUrl = $"https://{systemDomain}/oauth2/callback";
            var logoutUrl = $"https://{systemDomain}/oauth2/logout-callback";

            var userPoolClient = new UserPoolClient($"{poolPrefix}-client", new UserPoolClientArgs
            {
                Name = $"{poolPrefix}-client",
                UserPoolId = userPool.Id,
                GenerateSecret = false,
                ExplicitAuthFlows =
                {
                    "ALLOW_USER_SRP_AUTH",
                    "ALLOW_REFRESH_TOKEN_AUTH",
                    "ALLOW_USER_PASSWORD_AUTH",
                },
                SupportedIdentityProviders = { "COGNITO" },
                PreventUserExistenceErrors = "ENABLED",
                // OAuth / Hosted UI settings
                AllowedOauthFlows = { "code" },
                AllowedOauthScopes = { "openid", "profile", "email" },
                AllowedOauthFlowsUserPoolClient = true,
                CallbackUrls = { callbackUrl },
                LogoutUrls = { logoutUrl },
            }, new CustomResourceOptions { Parent = this });

            // =================================================================
            // ACM CERTIFICATE for custom domain (us-east-1)
            // =================================================================

            var cert = new Certificate($"{poolPrefix}-cert", new CertificateArgs
            {
                DomainName = customDomain,
                ValidationMethod = "DNS",
                Tags =
                {
                    { "System", sk },
                    { "AuthType", authType },
                    { "ManagedBy", "lz-pulumi" },
                },
            }, new CustomResourceOptions { Parent = this, Provider = usEast1 });

            var certValidationRecord = new Pulumi.Aws.Route53.Record($"{poolPrefix}-cert-val",
                new Pulumi.Aws.Route53.RecordArgs
                {
                    ZoneId = publicZone.Apply(z => z.ZoneId),
                    Name = cert.DomainValidationOptions.Apply(o => o[0].ResourceRecordName!),
                    Type = cert.DomainValidationOptions.Apply(o => o[0].ResourceRecordType!),
                    Records = { cert.DomainValidationOptions.Apply(o => o[0].ResourceRecordValue!) },
                    Ttl = 300,
                    AllowOverwrite = true,
                }, new CustomResourceOptions { Parent = this });

            var certValidation = new CertificateValidation($"{poolPrefix}-cert-validated",
                new CertificateValidationArgs
                {
                    CertificateArn = cert.Arn,
                    ValidationRecordFqdns = { certValidationRecord.Fqdn },
                }, new CustomResourceOptions { Parent = this, Provider = usEast1 });

            // =================================================================
            // COGNITO CUSTOM DOMAIN
            // =================================================================

            var userPoolDomain = new UserPoolDomain($"{poolPrefix}-domain", new UserPoolDomainArgs
            {
                Domain = customDomain,
                UserPoolId = userPool.Id,
                CertificateArn = certValidation.CertificateArn,
            }, new CustomResourceOptions { Parent = this });

            // Route 53 alias: auth.{domain} → Cognito's managed CloudFront distribution
            new Pulumi.Aws.Route53.Record($"{poolPrefix}-dns", new Pulumi.Aws.Route53.RecordArgs
            {
                ZoneId = publicZone.Apply(z => z.ZoneId),
                Name = customDomain,
                Type = "A",
                AllowOverwrite = true,
                Aliases =
                {
                    new Pulumi.Aws.Route53.Inputs.RecordAliasArgs
                    {
                        Name = userPoolDomain.CloudfrontDistribution,
                        // Cognito's CloudFront distribution is always in this hosted zone
                        ZoneId = "Z2FDTNDATAQYW2",
                        EvaluateTargetHealth = false,
                    },
                },
            }, new CustomResourceOptions { Parent = this });

            // =================================================================
            // IDENTITY POOL
            // =================================================================

            var identityPool = new IdentityPool($"{poolPrefix}-idpool",
                new IdentityPoolArgs
                {
                    IdentityPoolName = $"{sk}_{suffix}_{env}_{authType}",
                    AllowUnauthenticatedIdentities = false,
                    CognitoIdentityProviders =
                    {
                        new IdentityPoolCognitoIdentityProviderArgs
                        {
                            ClientId = userPoolClient.Id,
                            ProviderName = userPool.Endpoint,
                        },
                    },
                }, new CustomResourceOptions { Parent = this });

            // =================================================================
            // CLOUDWATCH LOGS
            // =================================================================

            new LogGroup($"{poolPrefix}-logs", new LogGroupArgs
            {
                Name = $"/aws/cognito/{poolPrefix}",
                RetentionInDays = config.AppRunner?.LogRetentionDays ?? 3,
                Tags =
                {
                    { "System", sk },
                    { "AuthType", authType },
                    { "ManagedBy", "lz-pulumi" },
                },
            }, new CustomResourceOptions { Parent = this });

            // Track outputs
            poolOutputs[authType] = new CognitoPoolOutputs
            {
                UserPoolId = userPool.Id,
                UserPoolClientId = userPoolClient.Id,
                IdentityPoolId = identityPool.Id,
                MetadataUrl = userPool.Id.Apply(id =>
                    $"https://cognito-idp.{region}.amazonaws.com/{id}/.well-known/openid-configuration"),
                HostedUIDomain = Output.Create($"https://{customDomain}"),
            };
        }

        return new AwsAppRunnerCognitoOutputs
        {
            ServiceId = poolOutputs.Values.LastOrDefault()?.UserPoolId ?? Output.Create(""),
            Endpoint = Output.Create($"cognito-idp.{region}.amazonaws.com"),
            AccessRoleArn = Output.Create(""),
            InstanceRoleArn = Output.Create(""),
            CognitoPools = poolOutputs,
        };
    }
}

/// <summary>
/// Per-pool outputs from the Cognito component.
/// </summary>
public class CognitoPoolOutputs
{
    public required Output<string> UserPoolId { get; init; }
    public required Output<string> UserPoolClientId { get; init; }
    public required Output<string> IdentityPoolId { get; init; }
    public required Output<string> MetadataUrl { get; init; }
    public required Output<string> HostedUIDomain { get; init; }
}

/// <summary>
/// Cognito service outputs implementing IAuthPoolOutputs.
/// </summary>
public class AwsAppRunnerCognitoOutputs : IAuthPoolOutputs
{
    public required Output<string> ServiceId { get; init; }
    public required Output<string> Endpoint { get; init; }
    public required Output<string> AccessRoleArn { get; init; }
    public required Output<string> InstanceRoleArn { get; init; }
    public required Dictionary<string, CognitoPoolOutputs> CognitoPools { get; init; }

    public Dictionary<string, AuthPoolDetail> Pools =>
        CognitoPools.ToDictionary(
            kv => kv.Key,
            kv => new AuthPoolDetail
            {
                UserPoolId = kv.Value.UserPoolId,
                ClientId = kv.Value.UserPoolClientId,
                MetadataUrl = kv.Value.MetadataUrl,
                HostedUIDomain = kv.Value.HostedUIDomain,
            });
}
