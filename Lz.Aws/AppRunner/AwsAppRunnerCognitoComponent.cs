using Lz.Core.Config;
using Lz.Aws.Config;
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

        if (config.AuthConfigs is null || config.AuthConfigs.Count == 0)
            throw new InvalidOperationException(
                $"No AuthConfigs declared in systemconfig for system '{config.SystemKey}'. " +
                $"The {config.Topology} topology requires at least one Cognito pool. " +
                "Add an AuthConfigs: block with pool names as keys.");
        var authTypes = config.AuthConfigs.Keys.ToList();

        // us-east-1 provider for ACM certs (Cognito custom domains use CloudFront internally)
        var usEast1 = new Provider($"{prefix}-cognito-us-east-1", new ProviderArgs
        {
            Region = "us-east-1",
        }, new CustomResourceOptions { Parent = this });

        // Route 53 zone for DNS validation and alias records
        var publicZone = Pulumi.Aws.Route53.GetZone.Invoke(
            new Pulumi.Aws.Route53.GetZoneInvokeArgs { Name = systemDomain });

        // Cognito's CreateUserPoolDomain API requires the parent (apex) domain
        // to have a resolvable A record; if absent, it rejects the custom
        // domain with "Custom domain is not a valid subdomain". Ensure a
        // placeholder A record at the apex. 127.0.0.1 is the canonical choice —
        // it doesn't serve anything and is trivially overridable by whatever
        // tenant stack later wants the apex to point somewhere real.
        // AllowOverwrite = true so re-applies on existing records are no-ops.
        //
        // Idempotence note: the tenant stack
        // (AwsEcsExpressCloudFrontComponent) overwrites this record with
        // an A-alias to its CloudFront distribution. The foundation must
        // NOT revert that on subsequent `lz deploysystem` runs — doing so
        // breaks the apex /oauth2/callback redirect that the auth flow
        // depends on (`https://{apex}` would resolve to 127.0.0.1 and
        // ECONNREFUSED). IgnoreChanges tells Pulumi to seed the record
        // on first create and then leave records/aliases/ttl alone on
        // every subsequent up. The tenant's alias survives.
        var apexPlaceholder = new Pulumi.Aws.Route53.Record($"{prefix}-apex-placeholder",
            new Pulumi.Aws.Route53.RecordArgs
            {
                ZoneId = publicZone.Apply(z => z.ZoneId),
                Name = systemDomain,
                Type = "A",
                Ttl = 300,
                Records = { "127.0.0.1" },
                AllowOverwrite = true,
            }, new CustomResourceOptions
            {
                Parent = this,
                IgnoreChanges = { "records", "aliases", "ttl" },
            });

        var poolOutputs = new Dictionary<string, CognitoPoolOutputs>();

        foreach (var authType in authTypes)
        {
            var poolPrefix = $"{prefix}-{authType}";
            var domainPrefix = DomainPrefixMap.GetValueOrDefault(authType, $"auth-{authType}");
            var customDomain = $"{domainPrefix}.{systemDomain}";

            // Per-pool hardening configuration. If the deserialised entry isn't
            // an AwsAuthConfigEntry (e.g. running under a non-AWS platform that
            // shouldn't reach this code path), fall back to a defaults instance.
            var poolConfig = config.AuthConfigs![authType] as AwsAuthConfigEntry
                ?? new AwsAuthConfigEntry();

            ValidatePoolConfig(authType, poolConfig);

            // =================================================================
            // USER POOL
            // =================================================================
            // DeviceConfiguration semantics (see AwsAuthConfigEntry.DeviceTracking):
            //   OFF          → no DeviceConfiguration (no device tracking)
            //   USER_OPT_IN  → tracking enabled, "Remember this device?" prompt in MFA flow
            //   ALWAYS       → tracking enabled, auto-remember (required for non-MFA pools)

            var userPoolArgs = new UserPoolArgs
            {
                Name = $"{sk}-{suffix}-{env}-{authType}",
                AutoVerifiedAttributes = { "email" },
                UsernameAttributes = { "email" },
                MfaConfiguration = poolConfig.MfaConfiguration,
                AdminCreateUserConfig = new UserPoolAdminCreateUserConfigArgs
                {
                    // AllowSelfSignUp inverts: true → users can self-register
                    // via Hosted UI's "Sign up" link; false → admin-only.
                    AllowAdminCreateUserOnly = !poolConfig.AllowSelfSignUp,
                },
                PasswordPolicy = new UserPoolPasswordPolicyArgs
                {
                    MinimumLength = poolConfig.PasswordMinLength,
                    RequireLowercase = true,
                    RequireUppercase = true,
                    RequireNumbers = true,
                    RequireSymbols = poolConfig.PasswordRequireSymbols,
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
            };

            // Device tracking — see AwsAuthConfigEntry.DeviceTracking for semantics.
            // ChallengeRequiredOnNewDevice = true is what enables device-key capture;
            // DeviceOnlyRememberedOnUserPrompt controls whether the user gets a prompt.
            if (!poolConfig.DeviceTracking.Equals("OFF", StringComparison.OrdinalIgnoreCase))
            {
                userPoolArgs.DeviceConfiguration = new UserPoolDeviceConfigurationArgs
                {
                    ChallengeRequiredOnNewDevice = true,
                    DeviceOnlyRememberedOnUserPrompt =
                        poolConfig.DeviceTracking.Equals("USER_OPT_IN", StringComparison.OrdinalIgnoreCase),
                };
            }

            // Software TOTP MFA — enabled per-pool when MfaConfiguration != OFF
            // and SoftwareTokenMfa is opted in. Requires no additional AWS
            // resources (unlike SMS MFA, which needs an SNS caller role).
            if (!poolConfig.MfaConfiguration.Equals("OFF", StringComparison.OrdinalIgnoreCase)
                && poolConfig.SoftwareTokenMfa)
            {
                userPoolArgs.SoftwareTokenMfaConfiguration = new UserPoolSoftwareTokenMfaConfigurationArgs
                {
                    Enabled = true,
                };
            }

            // Advanced Security Mode — opt-in per pool. ENFORCED incurs per-MAU
            // pricing; AUDIT logs risk events without enforcement.
            if (!poolConfig.AdvancedSecurityMode.Equals("OFF", StringComparison.OrdinalIgnoreCase))
            {
                userPoolArgs.UserPoolAddOns = new UserPoolUserPoolAddOnsArgs
                {
                    AdvancedSecurityMode = poolConfig.AdvancedSecurityMode,
                };
            }

            var userPool = new UserPool($"{poolPrefix}-pool", userPoolArgs,
                new CustomResourceOptions { Parent = this });

            // =================================================================
            // USER POOL CLIENT — with OAuth/Hosted UI configuration
            // =================================================================

            // Callback + logout URLs. Primary entries live on the tenant's root
            // domain (CFAuthCallback.js then redirects to the subtenant). When
            // IncludeDevCallbackUrls is true, dev-only entries (localhost) are
            // added — gated per pool so prod pools reject non-prod redirects.
            var callbackUrls = new List<string> { $"https://{systemDomain}/oauth2/callback" };
            var logoutUrls = new List<string> { $"https://{systemDomain}/oauth2/logout-callback" };
            if (poolConfig.IncludeDevCallbackUrls)
            {
                callbackUrls.Add("https://localhost:5001/authentication/login-callback");
                logoutUrls.Add("https://localhost:5001/authentication/logout-callback");
            }

            var userPoolClientArgs = new UserPoolClientArgs
            {
                Name = $"{poolPrefix}-client",
                UserPoolId = userPool.Id,
                GenerateSecret = false,
                // ALLOW_USER_PASSWORD_AUTH intentionally omitted — SRP is the
                // only browser-side flow we support. Dropping password auth
                // prevents plaintext-password submission to Cognito.
                ExplicitAuthFlows =
                {
                    "ALLOW_USER_SRP_AUTH",
                    "ALLOW_REFRESH_TOKEN_AUTH",
                },
                SupportedIdentityProviders = { "COGNITO" },
                PreventUserExistenceErrors = "ENABLED",
                // OAuth / Hosted UI settings
                AllowedOauthFlows = { "code" },
                AllowedOauthScopes = { "openid", "profile", "email" },
                AllowedOauthFlowsUserPoolClient = true,
                CallbackUrls = { callbackUrls.ToArray() },
                LogoutUrls = { logoutUrls.ToArray() },
            };
            var userPoolClient = new UserPoolClient($"{poolPrefix}-client", userPoolClientArgs,
                new CustomResourceOptions { Parent = this });

            // =================================================================
            // USER POOL GROUPS — role distinctions within the pool (roles
            // surface to the app in the cognito:groups JWT claim)
            // =================================================================

            if (poolConfig.Groups != null)
            {
                foreach (var group in poolConfig.Groups)
                {
                    if (string.IsNullOrWhiteSpace(group.Name))
                        throw new InvalidOperationException(
                            $"Pool '{authType}' has a group with empty Name. Check systemconfig.");

                    var groupArgs = new UserGroupArgs
                    {
                        Name = group.Name,
                        UserPoolId = userPool.Id,
                        Precedence = group.Precedence,
                    };
                    if (!string.IsNullOrWhiteSpace(group.Description))
                        groupArgs.Description = group.Description;
                    if (!string.IsNullOrWhiteSpace(group.RoleArn))
                        groupArgs.RoleArn = group.RoleArn;

                    _ = new UserGroup($"{poolPrefix}-group-{group.Name}", groupArgs,
                        new CustomResourceOptions { Parent = this });
                }
            }

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

            // ManagedLoginVersion=2 selects the new "Managed Login Pages"
            // hosted UI; version 1 is the legacy Hosted UI. We pin to 2
            // because the legacy version has a broken post-signup-confirm
            // OAuth continuation: the form's first Confirm Account click
            // succeeds (user → CONFIRMED, email_verified=true), but the
            // OAuth code-grant follow-up silently fails and the page
            // re-renders with a misleading "Invalid verification code"
            // error. Managed Login Pages handles the same flow correctly.
            // Verified empirically against this codebase's plannerauth
            // pool — see Platform/test/tests/diag-signup-confirm.spec.js.
            var userPoolDomain = new UserPoolDomain($"{poolPrefix}-domain", new UserPoolDomainArgs
            {
                Domain = customDomain,
                UserPoolId = userPool.Id,
                CertificateArn = certValidation.CertificateArn,
                ManagedLoginVersion = 2,
            }, new CustomResourceOptions
            {
                Parent = this,
                DependsOn = { apexPlaceholder },
            });

            // ManagedLoginVersion=2 requires a per-client branding to be
            // present, otherwise the hosted UI returns errors trying to
            // render the Sign-in / Sign-up / Confirm pages.
            //
            // Branding source-of-truth: a `Cognito/{authType}/` directory
            // at the working directory root (the consumer repo's root —
            // BCProjNew, etc.). Layout:
            //
            //   Cognito/{authType}/
            //     settings.json              ← JSON document for Settings
            //     assets/{light|dark|dynamic}/{category-kebab}.{ext}
            //
            // If no folder for this authType exists (or settings.json is
            // empty/`{}` and no assets), we fall back to
            // UseCognitoProvidedValues=true — Cognito's stock look.
            var brandingArgs = BuildBrandingArgsFromConventionFolder(
                userPool.Id, userPoolClient.Id, authType);
            new ManagedLoginBranding($"{poolPrefix}-branding", brandingArgs,
                new CustomResourceOptions
                {
                    Parent = this,
                    DependsOn = { userPoolDomain },
                    // Cognito enforces one branding per client. Toggling
                    // settings or assets is treated by the Pulumi/Terraform
                    // provider as a replacement, and the default
                    // create-before-replace order trips
                    // ManagedLoginBrandingExistsException because the slot
                    // is already taken. Force delete-before-create so the
                    // old branding is gone before the new one is created.
                    DeleteBeforeReplace = true,
                });

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
                RetentionInDays = AwsConfigMerger.GetEffectiveSystemLogRetentionDays(config),
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
                Authority = Output.Create($"https://{customDomain}"),
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

    /// <summary>
    /// Validates the hardening configuration for a single pool. Enum-like
    /// string fields are checked case-insensitively against Cognito's accepted
    /// values; SMS MFA is rejected because it requires an SNS caller role we
    /// don't currently provision.
    /// </summary>
    private static void ValidatePoolConfig(string authType, AwsAuthConfigEntry poolConfig)
    {
        // Pulumi/TF provider accepts OFF | ON | OPTIONAL (not REQUIRED).
        var validMfa = new[] { "OFF", "ON", "OPTIONAL" };
        if (!validMfa.Any(v => v.Equals(poolConfig.MfaConfiguration, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(
                $"Pool '{authType}' has invalid MfaConfiguration '{poolConfig.MfaConfiguration}'. " +
                $"Allowed: {string.Join(", ", validMfa)}.");

        var validAsm = new[] { "OFF", "AUDIT", "ENFORCED" };
        if (!validAsm.Any(v => v.Equals(poolConfig.AdvancedSecurityMode, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(
                $"Pool '{authType}' has invalid AdvancedSecurityMode '{poolConfig.AdvancedSecurityMode}'. " +
                $"Allowed: {string.Join(", ", validAsm)}.");

        var validDeviceTracking = new[] { "OFF", "ALWAYS", "USER_OPT_IN" };
        if (!validDeviceTracking.Any(v => v.Equals(poolConfig.DeviceTracking, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(
                $"Pool '{authType}' has invalid DeviceTracking '{poolConfig.DeviceTracking}'. " +
                $"Allowed: {string.Join(", ", validDeviceTracking)}.");

        // USER_OPT_IN with MFA OFF means the "Remember this device?" checkbox
        // never appears (Hosted UI only renders it during the MFA challenge),
        // so no device is ever remembered and the pool gets none of the
        // device-binding benefits. Fail loud — pick ALWAYS for non-MFA pools
        // that want device tracking, or OFF if you don't.
        if (poolConfig.DeviceTracking.Equals("USER_OPT_IN", StringComparison.OrdinalIgnoreCase)
            && poolConfig.MfaConfiguration.Equals("OFF", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Pool '{authType}' has DeviceTracking=USER_OPT_IN with MfaConfiguration=OFF. " +
                "The Hosted UI 'Remember this device?' prompt only appears during MFA, so this " +
                "combination tracks zero devices. Use DeviceTracking=ALWAYS for non-MFA pools " +
                "that want device-bound refresh tokens, or DeviceTracking=OFF to disable.");

        // Cognito rejects MinimumLength < 6 or > 99.
        if (poolConfig.PasswordMinLength < 6 || poolConfig.PasswordMinLength > 99)
            throw new InvalidOperationException(
                $"Pool '{authType}' has invalid PasswordMinLength {poolConfig.PasswordMinLength}. " +
                "Cognito requires a value between 6 and 99.");

        // SMS MFA requires an SNS role. Not provisioned today — fail loud if requested.
        if (poolConfig.SmsMfa && !poolConfig.MfaConfiguration.Equals("OFF", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Pool '{authType}' requests SmsMfa=true, but SMS MFA requires an SNS caller role " +
                "which the tool does not currently provision. Set SmsMfa: false and use " +
                "SoftwareTokenMfa: true (TOTP) instead. SMS MFA support is tracked as a follow-up.");

        // If MFA is on but no factor is enabled, the pool will be unusable —
        // Cognito will accept the setting but logins will fail.
        if (!poolConfig.MfaConfiguration.Equals("OFF", StringComparison.OrdinalIgnoreCase)
            && !poolConfig.SoftwareTokenMfa
            && !poolConfig.SmsMfa)
            throw new InvalidOperationException(
                $"Pool '{authType}' has MfaConfiguration={poolConfig.MfaConfiguration} but no MFA " +
                "factor enabled. Set SoftwareTokenMfa: true (or SmsMfa: true once SMS is supported).");
    }

    /// <summary>
    /// Build <see cref="ManagedLoginBrandingArgs"/> from the convention
    /// folder <c>Cognito/{authType}/</c> at the current working directory.
    /// If the folder doesn't exist (or contains nothing meaningful), falls
    /// back to <c>UseCognitoProvidedValues = true</c> — Cognito's stock look.
    /// <para>
    /// Layout the folder is expected to follow:
    /// <code>
    ///   Cognito/{authType}/
    ///     settings.json                   ← optional, JSON document
    ///     assets/{light|dark|dynamic}/{category-kebab}.{ext}
    /// </code>
    /// where <c>category-kebab</c> is the kebab-case lowercase form of a
    /// Cognito asset category (e.g. <c>page-header-logo</c> →
    /// <c>PAGE_HEADER_LOGO</c>) and the parent directory provides the
    /// <c>ColorMode</c>.
    /// </para>
    /// </summary>
    private static ManagedLoginBrandingArgs BuildBrandingArgsFromConventionFolder(
        Input<string> userPoolId, Input<string> clientId, string authType)
    {
        var brandingDir = Path.Combine(
            Directory.GetCurrentDirectory(), "Cognito", authType);

        if (!Directory.Exists(brandingDir))
        {
            return new ManagedLoginBrandingArgs
            {
                UserPoolId = userPoolId,
                ClientId = clientId,
                UseCognitoProvidedValues = true,
            };
        }

        // Read settings.json. Empty/missing → null → defer to defaults.
        string? settingsJson = null;
        var settingsPath = Path.Combine(brandingDir, "settings.json");
        if (File.Exists(settingsPath))
        {
            var raw = File.ReadAllText(settingsPath).Trim();
            // Empty file or `{}` is treated as "no custom settings"; using
            // UseCognitoProvidedValues=true keeps the stock Cognito look
            // and avoids sending a no-op Settings doc.
            if (raw.Length > 0 && raw != "{}")
                settingsJson = raw;
        }

        // Walk assets/. Each immediate child directory's name is the
        // ColorMode (LIGHT / DARK / DYNAMIC). Each file inside is named
        // after its category (kebab-case → SNAKE_CASE on the API side).
        var assetList = new List<Pulumi.Aws.Cognito.Inputs.ManagedLoginBrandingAssetArgs>();
        var assetsDir = Path.Combine(brandingDir, "assets");
        if (Directory.Exists(assetsDir))
        {
            foreach (var colorModeDir in Directory.GetDirectories(assetsDir))
            {
                var colorMode = Path.GetFileName(colorModeDir).ToUpperInvariant();
                if (colorMode != "LIGHT" && colorMode != "DARK" && colorMode != "DYNAMIC")
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine(
                        $"  Skipping unrecognised ColorMode dir '{colorMode}' under " +
                        $"Cognito/{authType}/assets/ (expected light|dark|dynamic).");
                    Console.ResetColor();
                    continue;
                }
                foreach (var file in Directory.GetFiles(colorModeDir))
                {
                    var name = Path.GetFileName(file);
                    // Skip Git placeholders.
                    if (name == ".gitkeep" || name.StartsWith('.')) continue;

                    var stem = Path.GetFileNameWithoutExtension(file);
                    var ext = Path.GetExtension(file).TrimStart('.').ToUpperInvariant();
                    var category = stem.Replace('-', '_').ToUpperInvariant();
                    var bytesB64 = Convert.ToBase64String(File.ReadAllBytes(file));

                    assetList.Add(new Pulumi.Aws.Cognito.Inputs.ManagedLoginBrandingAssetArgs
                    {
                        Bytes = bytesB64,
                        Category = category,
                        ColorMode = colorMode,
                        Extension = ext,
                    });
                }
            }
        }

        // If neither real settings nor assets were found, defer to defaults.
        if (settingsJson is null && assetList.Count == 0)
        {
            return new ManagedLoginBrandingArgs
            {
                UserPoolId = userPoolId,
                ClientId = clientId,
                UseCognitoProvidedValues = true,
            };
        }

        var args = new ManagedLoginBrandingArgs
        {
            UserPoolId = userPoolId,
            ClientId = clientId,
        };
        // Settings and UseCognitoProvidedValues are mutually exclusive on
        // the AWS API. We set Settings if we have any real customisation;
        // otherwise we leave Settings unset (and add UseCognitoProvidedValues=true
        // when neither Settings nor Assets are present, handled above).
        if (settingsJson is not null) args.Settings = settingsJson;
        if (assetList.Count > 0) args.Assets = assetList;
        return args;
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
    public required Output<string> Authority { get; init; }
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
                Authority = kv.Value.Authority,
            });
}
