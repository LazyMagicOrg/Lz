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
using Pulumi.Aws.DynamoDB;
using Pulumi.Aws.DynamoDB.Inputs;
using LzDynamoDb = Lz.Aws.DynamoDB;
using Pulumi.Aws.Iam;
using Pulumi.Aws.Lambda;
using Pulumi.Aws.Lambda.Inputs;
using Lz.Aws.Compute.Fargate;
using Lz.Aws.Compute.FargateAlb;
using Lz.Aws.Compute.Lambda;
using Lz.Aws.Data;
using Lz.Aws.Edge;
using Lz.Aws.Ops;
using Lz.Aws.Shared;
using Lz.Aws.Storage;
using Lz.Aws.Tailscale;
using Lz.Aws.Topologies;
using Lz.Aws.Interfaces;
using Lz.Aws.Interfaces.Outputs;

namespace Lz.Aws.Auth;

/// <summary>
/// Cognito auth component with custom domains, device tracking, and Hosted UI support.
/// Creates user pools, clients, identity pools, custom domains, and ACM certs.
/// Custom domain mapping:
///   tenantauth  → auth.{systemDomain}
///   plannerauth → auth-planner.{systemDomain}
///   systemauth  → auth-system.{systemDomain}
/// </summary>
public class AwsCognitoComponent : ComponentResource, IAuthServiceComponent
{
    // Maps auth type to custom domain prefix
    private static readonly Dictionary<string, string> DomainPrefixMap = new()
    {
        ["tenantauth"] = "auth",
        ["plannerauth"] = "auth-planner",
        ["systemauth"] = "auth-system",
    };

    public AwsCognitoComponent()
        // FROZEN Pulumi type token: deployed-state URN identity — deliberately NOT
        // renamed in the 0.11.0 axis restructure (renaming would replace deployed
        // resources). See Lz/Migrations/AxisRestructure.md.
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
        // (AwsCloudFrontKvsComponent) overwrites this record with
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

            // Advanced Security Mode — opt-in per pool. Under the feature-tier
            // model (2024-11+), ANY non-OFF value requires — and silently lands
            // the pool on — the PLUS tier, which bills every MAU at the Plus
            // rate with no free tier. AUDIT-only telemetry additionally needs a
            // LogDeliveryConfiguration to go anywhere; without one it is pure
            // per-MAU cost. Prefer leaving this OFF unless threat protection is
            // actually consumed, and see UserPoolTier below to manage the tier
            // explicitly.
            if (!poolConfig.AdvancedSecurityMode.Equals("OFF", StringComparison.OrdinalIgnoreCase))
            {
                userPoolArgs.UserPoolAddOns = new UserPoolUserPoolAddOnsArgs
                {
                    AdvancedSecurityMode = poolConfig.AdvancedSecurityMode,
                };
            }

            // Explicit feature tier — opt-in. Null = tier not managed (the
            // byte-identical baseline: Pulumi sends no tier, existing pools
            // keep theirs). Set ESSENTIALS to unpin a pool that a since-removed
            // AdvancedSecurityMode left on PLUS: rotation + TOTP survive the
            // downgrade; threat protection does not (it needs PLUS, which is
            // why the validator refuses the contradictory combination).
            if (!string.IsNullOrEmpty(poolConfig.UserPoolTier))
                userPoolArgs.UserPoolTier = poolConfig.UserPoolTier.ToUpperInvariant();

            // Deletion protection — same Durability opt-in as the DynamoDB tables. Null
            // (section absent) leaves the property UNSET, so an un-opted-in system emits
            // the plan it emitted before this existed; that guarantee is what six
            // workspaces run on, so it is honoured literally rather than approximately
            // (INACTIVE is the service default, but assigning it is not the same as not
            // assigning it). `deletion_protection` is NOT ForceNew in the provider — only
            // alias_attributes, username_attributes and username_configuration.case_sensitive
            // are — so this is an in-place UpdateUserPool on an existing pool, never a
            // replace. Verified against the bridged schema of the pinned plugin and the
            // upstream provider source; the preview is still the gate (see
            // CognitoDurabilityPolicy).
            var poolDeletionProtection = CognitoDurabilityPolicy.ForUserPool(config.Durability);
            if (poolDeletionProtection is not null)
                userPoolArgs.DeletionProtection = poolDeletionProtection;

            // =================================================================
            // M0-7 — SELLER CUSTOM-AUTH INFRA (opt-in). Provisioned ONLY when poolConfig.CustomAuth is set,
            // so absent = byte-identical baseline for the ~10 sibling pools. The Lambda + its role + the
            // vendor-credential table are created HERE, BEFORE the pool, because the pool's LambdaConfig — a
            // POOL-LEVEL setting — must reference the Lambda ARN. The Cognito invoke Permission and the
            // seller/buyer clients need the pool, so they follow it below under the same guard.
            // =================================================================
            const string vendorCredKeyAttr = "username";
            const string vendorCredHashAttr = "apiKeyHash";
            Function? customAuthFn = null;
            if (poolConfig.CustomAuth is { } customAuth)
            {
                // Durability (Hygiene/Durability opt-in). This table holds vendor API-key
                // HASHES seeded by `lz provisionvendor` and held nowhere else — a rotation
                // overwrites the row and nothing retains the prior value. It is also the one
                // DynamoDB table in this topology that Pulumi owns, so an ordinary replace
                // would delete it. DeletionProtection turns that into a loud failure instead
                // of silent data loss; PITR makes a forced delete recoverable. Absent the
                // Durability section this is TableDurabilityDecision.None and the emitted
                // plan is byte-identical to a pre-durability deploy.
                var vendorCredDurability = LzDynamoDb.TableDurabilityPolicy.ForVendorCredTable(config.Durability);
                var vendorCredArgs = new TableArgs
                {
                    Name = $"{poolPrefix}-vendor-creds",
                    BillingMode = "PAY_PER_REQUEST",
                    HashKey = vendorCredKeyAttr,
                    Attributes = { new TableAttributeArgs { Name = vendorCredKeyAttr, Type = "S" } },
                    Tags = { { "System", sk }, { "ManagedBy", "lz-pulumi" } },
                };
                // Assign ONLY when opted in. Assigning an explicit `false` is not the same
                // as leaving the property unset: a system with no Durability section must
                // emit the plan it emitted before this existed, and "byte-identical when
                // absent" is a literal guarantee rather than an approximate one. (This
                // corrects the first cut of this change, which set both unconditionally.)
                if (vendorCredDurability.DeletionProtection)
                    vendorCredArgs.DeletionProtectionEnabled = true;
                if (vendorCredDurability.PointInTimeRecovery)
                    vendorCredArgs.PointInTimeRecovery = new TablePointInTimeRecoveryArgs { Enabled = true };

                var vendorCredTable = new Table($"{poolPrefix}-vendor-creds", vendorCredArgs,
                    new CustomResourceOptions { Parent = this });

                // Least-privilege role for the challenge Lambda: assumed by Lambda, writes its own logs, and
                // GetItem ONLY on the vendor-credential table (nothing else).
                var fnRole = new Role($"{poolPrefix}-custom-auth-role", new RoleArgs
                {
                    AssumeRolePolicy =
                        "{\"Version\":\"2012-10-17\",\"Statement\":[{\"Effect\":\"Allow\"," +
                        "\"Principal\":{\"Service\":\"lambda.amazonaws.com\"},\"Action\":\"sts:AssumeRole\"}]}",
                    Tags = { { "System", sk }, { "ManagedBy", "lz-pulumi" } },
                }, new CustomResourceOptions { Parent = this });

                _ = new RolePolicyAttachment($"{poolPrefix}-custom-auth-logs", new RolePolicyAttachmentArgs
                {
                    Role = fnRole.Name,
                    PolicyArn = "arn:aws:iam::aws:policy/service-role/AWSLambdaBasicExecutionRole",
                }, new CustomResourceOptions { Parent = this });

                _ = new RolePolicy($"{poolPrefix}-custom-auth-ddb", new RolePolicyArgs
                {
                    Role = fnRole.Id,
                    Policy = vendorCredTable.Arn.Apply(arn =>
                        "{\"Version\":\"2012-10-17\",\"Statement\":[{\"Effect\":\"Allow\"," +
                        "\"Action\":\"dynamodb:GetItem\",\"Resource\":\"" + arn + "\"}]}"),
                }, new CustomResourceOptions { Parent = this });

                // Inline Node.js source dir shipped next to the assembly (see Lz.Aws.csproj). We resolve it via
                // the assembly's own Location — NOT AppContext.BaseDirectory — because under plugin load the
                // running process is the lz runner, whose base dir does NOT contain Lz.Aws's content. This is
                // the exact idiom AwsGateCheckerLambdaComponent uses for gate-checker.zip.
                var customAuthDir = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(typeof(AwsCognitoComponent).Assembly.Location)!,
                    "CognitoCustomAuth");
                customAuthFn = new Function($"{poolPrefix}-custom-auth", new FunctionArgs
                {
                    Name = $"{poolPrefix}-custom-auth",
                    // Keep on a SUPPORTED Node.js runtime — nodejs20.x is deprecated (2026-04-30). nodejs24.x
                    // is the newest managed runtime (support to 2028-04-30) and, like all supported Node.js
                    // runtimes, bundles the AWS SDK for JavaScript v3 — which custom-auth.mjs relies on
                    // (`import('@aws-sdk/client-dynamodb')`, no node_modules shipped). The file is explicit
                    // .mjs (ESM), so Node 22/24's module-detection default is irrelevant. Bump before the
                    // next deprecation; verify the target still bundles the SDK v3.
                    Runtime = "nodejs24.x",
                    Handler = "custom-auth.handler",
                    // No build step, no npm deps (only the runtime-bundled AWS SDK v3); Pulumi zips the dir.
                    Code = new FileArchive(customAuthDir),
                    Role = fnRole.Arn,
                    Timeout = 5,   // Cognito's synchronous-trigger budget.
                    Environment = new FunctionEnvironmentArgs
                    {
                        Variables =
                        {
                            { "VENDOR_CRED_TABLE", vendorCredTable.Name },
                            { "VENDOR_CRED_KEY_ATTR", vendorCredKeyAttr },
                            { "VENDOR_CRED_HASH_ATTR", vendorCredHashAttr },
                        },
                    },
                    Tags = { { "System", sk }, { "ManagedBy", "lz-pulumi" } },
                }, new CustomResourceOptions { Parent = this });

                // The three challenge triggers → the one Lambda. POOL-LEVEL, so strictly inside this guard.
                userPoolArgs.LambdaConfig = new UserPoolLambdaConfigArgs
                {
                    DefineAuthChallenge = customAuthFn.Arn,
                    CreateAuthChallenge = customAuthFn.Arn,
                    VerifyAuthChallengeResponse = customAuthFn.Arn,
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
                // 5001 is the LocalWebService (API) port; 7218 is the Blazor WASM
                // dev-server port (all three MagicPets WASM apps launch on 7218 —
                // one at a time — per each WASMApp/Properties/launchSettings.json).
                // SPA-OIDC in local VS debug builds its redirect_uri from the WASM's
                // own origin+base-path (redirect override in Program.cs →
                // {BaseAddress}authentication/login-callback), so every localhost
                // base path a WASM can mount at MUST be registered or Cognito
                // returns redirect_mismatch. The bare-root entries cover an app
                // served at "/"; the per-app sub-paths (/store/,/admin/,/app/)
                // cover apps whose WASMApp.csproj StaticWebAssetBasePath mounts the
                // dev app under the same path as the cloud. Cognito has no wildcard
                // support, so each must be enumerated. Dev-only (IncludeDevCallbackUrls).
                callbackUrls.Add("https://localhost:5001/authentication/login-callback");
                logoutUrls.Add("https://localhost:5001/authentication/logout-callback");
                foreach (var basePath in new[] { "", "store/", "admin/", "app/" })
                {
                    callbackUrls.Add($"https://localhost:7218/{basePath}authentication/login-callback");
                    logoutUrls.Add($"https://localhost:7218/{basePath}authentication/logout-callback");
                }
                // MAUI native apps: LazyMagic.OIDC.MAUI hardcodes this custom-scheme
                // redirect (MauiOIDCService.GetRedirectUri → "awsloginmaui://auth-callback").
                // The callback is intercepted by the app's embedded WebView (no OS deep
                // link / intent-filter needed), but Cognito must have the scheme
                // registered on the client or the authorize request fails
                // redirect_mismatch. Registered on both lists (login + logout/end-session).
                callbackUrls.Add("awsloginmaui://auth-callback");
                logoutUrls.Add("awsloginmaui://auth-callback");
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
            // BFF CONFIDENTIAL CLIENT (additive, flag-gated) — §8.5
            // =================================================================
            // A SECOND app client for the Backend-For-Frontend auth flow. The
            // public client above is left untouched (flipping GenerateSecret is
            // immutable → replacement → breaks consumers). This client is
            // confidential (GenerateSecret=true) and used only by the BFF
            // server: authorization-code+PKCE exchange happens server-side, so
            // the secret never reaches the browser.
            //
            // Created ONLY when ProvisionBffClient==true. When the flag is off
            // (the default), neither this client nor its secret/outputs exist,
            // so the deploy plan is identical to a pre-BFF deploy.
            Output<string>? bffClientId = null;
            Output<string>? bffClientSecret = null;
            if (poolConfig.ProvisionBffClient)
            {
                // Token validity derived from the per-pool BFF session TTL.
                // RefreshTokenValidity == session lifetime (hours); access/id
                // tokens stay short (revocation-latency bound, §8.4/§8.14).
                var ttlHours = poolConfig.BffSessionTtlHours > 0
                    ? poolConfig.BffSessionTtlHours
                    : 12;

                // BFF callback lives at /bff/callback (NOT /oauth2/callback,
                // which is the façade's apex callback for the public client).
                // Mirror the public client's URL-building: primary on the
                // system apex; dev entries (localhost) when IncludeDevCallbackUrls.
                // Route prefix is per-pool (tenantauth=/bff; a second pool on the same apphost,
                // e.g. consumerauth, uses /cbff) so the two BFF instances' callbacks never collide.
                var bffPrefix = "/" + (poolConfig.BffRoutePrefix ?? "/bff").Trim('/');
                var bffCallbackUrls = new List<string> { $"https://{systemDomain}{bffPrefix}/callback" };
                var bffLogoutUrls = new List<string> { $"https://{systemDomain}{bffPrefix}/logout-callback" };
                if (poolConfig.IncludeDevCallbackUrls)
                {
                    bffCallbackUrls.Add($"https://localhost:5001{bffPrefix}/callback");
                    bffLogoutUrls.Add($"https://localhost:5001{bffPrefix}/logout-callback");
                }

                var bffClient = new UserPoolClient($"{poolPrefix}-bff-client", new UserPoolClientArgs
                {
                    Name = $"{poolPrefix}-bff-client",
                    UserPoolId = userPool.Id,
                    GenerateSecret = true,
                    // Refresh-token flow only — the BFF does code+PKCE at the
                    // /token endpoint and refreshes server-side. No browser SRP.
                    ExplicitAuthFlows = { "ALLOW_REFRESH_TOKEN_AUTH" },
                    SupportedIdentityProviders = { "COGNITO" },
                    PreventUserExistenceErrors = "ENABLED",
                    AllowedOauthFlows = { "code" },
                    AllowedOauthScopes = { "openid", "profile", "email" },
                    AllowedOauthFlowsUserPoolClient = true,
                    CallbackUrls = { bffCallbackUrls.ToArray() },
                    LogoutUrls = { bffLogoutUrls.ToArray() },
                    // Explicit token validity. Refresh == session TTL; access
                    // and id tokens kept short (60 min, Cognito min/typical).
                    RefreshTokenValidity = ttlHours,
                    AccessTokenValidity = 60,
                    IdTokenValidity = 60,
                    TokenValidityUnits = new UserPoolClientTokenValidityUnitsArgs
                    {
                        RefreshToken = "hours",
                        AccessToken = "minutes",
                        IdToken = "minutes",
                    },
                }, new CustomResourceOptions { Parent = this });

                bffClientId = bffClient.Id;
                bffClientSecret = bffClient.ClientSecret;
            }

            // =================================================================
            // MACHINE (client_credentials) CLIENTS — opt-in M2M principals.
            // Created ONLY when MachineAuth declares clients. When absent (the
            // default), no resource server, scope, or M2M client exists, so the
            // deploy plan is byte-for-byte identical to before (same guarantee
            // as the BFF client). A resource server declares the custom scopes;
            // each machine client is a NEW confidential app client — Cognito
            // forbids client_credentials on the public/BFF clients. M0-2.
            // =================================================================
            if (poolConfig.MachineAuth is { Clients.Count: > 0 } machineAuth)
            {
                if (string.IsNullOrWhiteSpace(machineAuth.Identifier))
                    throw new InvalidOperationException(
                        $"Pool '{authType}' MachineAuth declares clients but no Identifier. " +
                        "Set the resource-server identifier (the scope audience prefix).");

                var resourceServer = new ResourceServer($"{poolPrefix}-resource-server", new ResourceServerArgs
                {
                    Identifier = machineAuth.Identifier,
                    Name = $"{poolPrefix}-resource-server",
                    UserPoolId = userPool.Id,
                    Scopes = machineAuth.Scopes.Select(s => new ResourceServerScopeArgs
                    {
                        ScopeName = s.Name,
                        ScopeDescription = string.IsNullOrWhiteSpace(s.Description) ? s.Name : s.Description,
                    }).ToArray(),
                }, new CustomResourceOptions { Parent = this });

                var accessMinutes = machineAuth.AccessTokenMinutes > 0 ? machineAuth.AccessTokenMinutes : 60;
                foreach (var mc in machineAuth.Clients)
                {
                    if (string.IsNullOrWhiteSpace(mc.Name))
                        throw new InvalidOperationException(
                            $"Pool '{authType}' has a MachineAuth client with empty Name. Check systemconfig.");

                    // Cognito requires scopes on a client_credentials client to be
                    // resource-server-qualified: "{identifier}/{scope}".
                    var qualifiedScopes = mc.Scopes.Select(sc => $"{machineAuth.Identifier}/{sc}").ToArray();

                    _ = new UserPoolClient($"{poolPrefix}-m2m-{mc.Name}", new UserPoolClientArgs
                    {
                        Name = $"{poolPrefix}-m2m-{mc.Name}",
                        UserPoolId = userPool.Id,
                        GenerateSecret = true,
                        // client_credentials ONLY — no user-auth flow, no callback/logout URLs.
                        AllowedOauthFlows = { "client_credentials" },
                        AllowedOauthScopes = { qualifiedScopes },
                        AllowedOauthFlowsUserPoolClient = true,
                        AccessTokenValidity = accessMinutes,
                        TokenValidityUnits = new UserPoolClientTokenValidityUnitsArgs
                        {
                            AccessToken = "minutes",
                        },
                    }, new CustomResourceOptions { Parent = this, DependsOn = { resourceServer } });
                }
            }

            // =================================================================
            // M0-7 — SELLER/BUYER CLIENTS + the Cognito invoke Permission (opt-in; the pool + Lambda exist
            // now). Same guard as the pre-pool infra above.
            // =================================================================
            if (poolConfig.CustomAuth is { } customAuthClients && customAuthFn is not null)
            {
                // Cognito must be allowed to invoke the challenge Lambda; scope the permission to THIS pool so
                // no other pool (or account principal) can trigger it.
                _ = new Permission($"{poolPrefix}-custom-auth-invoke", new PermissionArgs
                {
                    Action = "lambda:InvokeFunction",
                    Function = customAuthFn.Name,
                    Principal = "cognito-idp.amazonaws.com",
                    SourceArn = userPool.Arn,
                }, new CustomResourceOptions { Parent = this });

                // The confidential SELLER client: non-interactive Custom-Auth (the vendor presents its API key,
                // validated by the challenge Lambda) + the ADMIN_USER_PASSWORD_AUTH MVP fallback — both yield a
                // USER token (sub = the vendor user). GenerateSecret=true (a server-side agent holds it).
                var sellerSuffix = string.IsNullOrWhiteSpace(customAuthClients.SellerClientName)
                    ? "seller-agent" : customAuthClients.SellerClientName;
                _ = new UserPoolClient($"{poolPrefix}-{sellerSuffix}-client", new UserPoolClientArgs
                {
                    Name = $"{poolPrefix}-{sellerSuffix}-client",
                    UserPoolId = userPool.Id,
                    GenerateSecret = true,
                    ExplicitAuthFlows =
                    {
                        "ALLOW_CUSTOM_AUTH",
                        "ALLOW_ADMIN_USER_PASSWORD_AUTH",   // MVP fallback — same USER identity as Custom-Auth
                        "ALLOW_REFRESH_TOKEN_AUTH",
                    },
                    SupportedIdentityProviders = { "COGNITO" },
                    PreventUserExistenceErrors = "ENABLED",
                    AccessTokenValidity = customAuthClients.AccessTokenMinutes > 0 ? customAuthClients.AccessTokenMinutes : 60,
                    TokenValidityUnits = new UserPoolClientTokenValidityUnitsArgs { AccessToken = "minutes" },
                }, new CustomResourceOptions { Parent = this });

                // Optional PUBLIC buyer/device client: Auth Code + PKCE (public ⇒ Cognito enforces PKCE),
                // for on-device buyer agents (§ McpAuth.md).
                if (!string.IsNullOrWhiteSpace(customAuthClients.BuyerDeviceClientName))
                {
                    var buyerDays = customAuthClients.BuyerRefreshTokenDays > 0 ? customAuthClients.BuyerRefreshTokenDays : 90;
                    _ = new UserPoolClient($"{poolPrefix}-{customAuthClients.BuyerDeviceClientName}-client", new UserPoolClientArgs
                    {
                        Name = $"{poolPrefix}-{customAuthClients.BuyerDeviceClientName}-client",
                        UserPoolId = userPool.Id,
                        GenerateSecret = false,
                        // ExplicitAuthFlows OMITTED (see the MCP client): an empty-list drifts against Cognito's
                        // null; OAuth-only client needs no InitiateAuth flows.
                        SupportedIdentityProviders = { "COGNITO" },
                        PreventUserExistenceErrors = "ENABLED",
                        AllowedOauthFlows = { "code" },
                        AllowedOauthScopes = { "openid", "profile", "email" },
                        AllowedOauthFlowsUserPoolClient = true,
                        CallbackUrls = { callbackUrls.ToArray() },
                        LogoutUrls = { logoutUrls.ToArray() },
                        RefreshTokenValidity = buyerDays,
                        AccessTokenValidity = 60,
                        IdTokenValidity = 60,
                        TokenValidityUnits = new UserPoolClientTokenValidityUnitsArgs
                        {
                            RefreshToken = "days",
                            AccessToken = "minutes",
                            IdToken = "minutes",
                        },
                        // Refresh-token ROTATION enabled (see the MCP client) — Feature + RetryGracePeriodSeconds
                        // both required for a clean round-trip.
                        RefreshTokenRotation = new UserPoolClientRefreshTokenRotationArgs
                        {
                            Feature = "ENABLED",
                            RetryGracePeriodSeconds = 60,
                        },
                    }, new CustomResourceOptions
                    {
                        Parent = this,
                        IgnoreChanges = { "explicitAuthFlows" },
                    });
                }
            }

            // =================================================================
            // SMARTSTORE CONFIDENTIAL CLIENT (additive, flag-gated) — §8
            // =================================================================
            // A confidential app client for the Smartstore storefront's OpenID
            // Connect handler (the Smartstore.Cognito.Auth module). Unlike the
            // BFF client, its callback is the framework-owned sign-in path at the
            // store apex (/signin-cognito) and its sign-out URL is the apex root
            // (Cognito has no end_session_endpoint; the module hits {domain}/logout
            // with logout_uri=apex). Confidential (GenerateSecret=true): the code
            // exchange runs server-side in the container, so the secret never
            // reaches the browser.
            //
            // Created ONLY when ProvisionSmartstoreClient==true. When off (the
            // default) neither this client nor its secret/branding/outputs exist,
            // so the deploy plan is identical to a pre-Smartstore deploy.
            Output<string>? smartstoreClientId = null;
            Output<string>? smartstoreClientSecret = null;
            if (poolConfig.ProvisionSmartstoreClient)
            {
                // Callback == the module's fixed CallbackPath (/signin-cognito) on
                // the store apex; sign-out == the apex root. Dev entries (localhost
                // on the storefront's container port) only when IncludeDevCallbackUrls.
                var ssCallbackUrls = new List<string> { $"https://{systemDomain}/signin-cognito" };
                var ssLogoutUrls = new List<string> { $"https://{systemDomain}/" };
                if (poolConfig.IncludeDevCallbackUrls)
                {
                    ssCallbackUrls.Add("https://localhost:8080/signin-cognito");
                    ssLogoutUrls.Add("https://localhost:8080/");
                }

                var smartstoreClient = new UserPoolClient($"{poolPrefix}-smartstore-client", new UserPoolClientArgs
                {
                    Name = $"{poolPrefix}-smartstore-client",
                    UserPoolId = userPool.Id,
                    GenerateSecret = true,
                    // Refresh-token flow only — the storefront does code+PKCE at
                    // the /token endpoint and refreshes server-side.
                    ExplicitAuthFlows = { "ALLOW_REFRESH_TOKEN_AUTH" },
                    SupportedIdentityProviders = { "COGNITO" },
                    PreventUserExistenceErrors = "ENABLED",
                    AllowedOauthFlows = { "code" },
                    AllowedOauthScopes = { "openid", "profile", "email" },
                    AllowedOauthFlowsUserPoolClient = true,
                    CallbackUrls = { ssCallbackUrls.ToArray() },
                    LogoutUrls = { ssLogoutUrls.ToArray() },
                }, new CustomResourceOptions
                {
                    Parent = this,
                    // generateSecret is create-only AND unreadable: the Cognito API never
                    // returns it, so `pulumi import` cannot capture it and leaves state at the
                    // schema default (false). Because generateSecret is ForceNew, the next plan
                    // would see false→true and REPLACE the client — rotating the client id the
                    // Smartstore OIDC module depends on. This client is explicitly designed to
                    // be adopted (import an already-provisioned confidential client), so ignore
                    // the drift. No effect on a fresh create: IgnoreChanges only gates updates,
                    // and the secret is still generated at create time.
                    IgnoreChanges = { "generateSecret" },
                });

                smartstoreClientId = smartstoreClient.Id;
                smartstoreClientSecret = smartstoreClient.ClientSecret;
            }

            // =================================================================
            // M0-8 — MCP RESOURCE SERVER + PUBLIC PKCE CLIENT (opt-in). A Cognito resource server whose
            // identifier IS the MCP URL (so a token can carry aud == that URL under RFC 8707), plus a PUBLIC
            // auth-code + PKCE client granted the qualified scope. Absent ⇒ byte-identical baseline. Distinct
            // from MachineAuth: that mints client_credentials clients (no sub, no aud); the hosted MCP path
            // needs auth-code + PKCE — the only Cognito flow yielding sub + scope + aud together. The aud
            // itself is set at RUNTIME (client sends &resource=<Identifier> at /authorize), not here. See
            // specs/McpAuth.md §7.4 and specs/McpAgents.md M0-8.
            // =================================================================
            // Captured for the ManagedLoginBranding block below: ManagedLoginVersion=2 requires a per-client
            // branding slot or the hosted UI returns "Login pages unavailable" for THIS client's sign-in (the
            // buyer/device agent's one-time PKCE login). Null unless McpResource was configured for this pool.
            Output<string>? mcpClientId = null;
            // Per-surface connector clients (BuyerOnboarding.md #9) — each needs its own branding slot
            // below, so (name, id) pairs are collected here. Empty unless SurfaceClients are configured.
            var mcpSurfaceClientIds = new List<(string Name, Output<string> Id)>();
            if (poolConfig.McpResource is { } mcp)
            {
                if (string.IsNullOrWhiteSpace(mcp.Identifier))
                    throw new InvalidOperationException(
                        $"Pool '{authType}' McpResource has no Identifier — set the MCP endpoint URL " +
                        "(it is BOTH the resource-server id and the token aud).");

                var mcpScope = string.IsNullOrWhiteSpace(mcp.Scope) ? "invoke" : mcp.Scope;

                var mcpResourceServer = new ResourceServer($"{poolPrefix}-mcp-resource-server", new ResourceServerArgs
                {
                    Identifier = mcp.Identifier,
                    Name = $"{poolPrefix}-mcp-resource-server",
                    UserPoolId = userPool.Id,
                    Scopes =
                    {
                        new ResourceServerScopeArgs
                        {
                            ScopeName = mcpScope,
                            ScopeDescription = string.IsNullOrWhiteSpace(mcp.ScopeDescription) ? mcpScope : mcp.ScopeDescription,
                        },
                    },
                }, new CustomResourceOptions { Parent = this });

                // Token-visible qualified scope "{identifier}/{scope}" — what the client requests, Cognito puts
                // in the scope claim, and AipHost's McpTokenGuard checks. Depends on the resource server.
                var qualifiedMcpScope = $"{mcp.Identifier}/{mcpScope}";
                var mcpClientSuffix = string.IsNullOrWhiteSpace(mcp.ClientName) ? "mcp" : mcp.ClientName;
                var mcpDays = mcp.RefreshTokenDays > 0 ? mcp.RefreshTokenDays : 90;

                var mcpClient = new UserPoolClient($"{poolPrefix}-{mcpClientSuffix}-client", new UserPoolClientArgs
                {
                    Name = $"{poolPrefix}-{mcpClientSuffix}-client",
                    UserPoolId = userPool.Id,
                    GenerateSecret = false,   // public ⇒ Cognito enforces PKCE (S256); works for device + server
                    // ExplicitAuthFlows intentionally OMITTED (not empty-list): Cognito stores "no flows" as
                    // null, and an empty-list in the program drifts perpetually against that null. This is an
                    // OAuth-only client (refresh via /oauth2/token), so it needs no InitiateAuth flows.
                    SupportedIdentityProviders = { "COGNITO" },
                    PreventUserExistenceErrors = "ENABLED",
                    AllowedOauthFlows = { "code" },
                    // openid/profile/email for the user identity + the qualified MCP scope for the resource.
                    AllowedOauthScopes = { "openid", "profile", "email", qualifiedMcpScope },
                    AllowedOauthFlowsUserPoolClient = true,
                    CallbackUrls = { callbackUrls.ToArray() },
                    LogoutUrls = { logoutUrls.ToArray() },
                    RefreshTokenValidity = mcpDays,
                    AccessTokenValidity = 60,
                    IdTokenValidity = 60,
                    TokenValidityUnits = new UserPoolClientTokenValidityUnitsArgs
                    {
                        RefreshToken = "days",
                        AccessToken = "minutes",
                        IdToken = "minutes",
                    },
                    // Refresh-token ROTATION enabled — a stolen refresh token is then single-use. BOTH Feature
                    // AND RetryGracePeriodSeconds must be set: Feature alone leaves the block incomplete and it
                    // never round-trips (that mis-read as "the plan ignores it" — the pool is actually on the
                    // PLUS tier, which supports rotation). The 60s grace tolerates one in-flight retry with the
                    // just-rotated token.
                    RefreshTokenRotation = new UserPoolClientRefreshTokenRotationArgs
                    {
                        Feature = "ENABLED",
                        RetryGracePeriodSeconds = 60,
                    },
                }, new CustomResourceOptions
                {
                    Parent = this,
                    DependsOn = { mcpResourceServer },
                    // Only explicitAuthFlows is ignored: this OAuth-only client sets no InitiateAuth flows, and
                    // Cognito stores "no flows" as null while the provider would send an empty-list → drift.
                    // Rotation is MANAGED (not ignored) so Pulumi enforces it and drift means it's really off.
                    IgnoreChanges = { "explicitAuthFlows" },
                });

                mcpClientId = mcpClient.Id;

                // =============================================================
                // PER-SURFACE STATIC PKCE CLIENTS (BuyerOnboarding.md #9) — one
                // per assistant connector directory (Claude first). Cognito has
                // no DCR/CIMD, so each directory gets a dedicated hand-registered
                // client id conveyed out-of-band, carrying that surface's EXACT
                // redirect URI(s) (Cognito refuses any non-exact redirect). Same
                // public-PKCE posture as the base MCP client — auth-code only,
                // qualified MCP scope, refresh rotation — differing only in name
                // and callbacks. Additive: no SurfaceClients ⇒ byte-identical.
                // =============================================================
                foreach (var surface in mcp.SurfaceClients)
                {
                    if (string.IsNullOrWhiteSpace(surface.Name))
                        throw new InvalidOperationException(
                            $"Pool '{authType}' McpResource has a SurfaceClient with empty Name. Check systemconfig.");
                    if (surface.CallbackUrls.Count == 0 || surface.CallbackUrls.Any(string.IsNullOrWhiteSpace))
                        throw new InvalidOperationException(
                            $"Pool '{authType}' McpResource SurfaceClient '{surface.Name}' needs at least one " +
                            "non-empty CallbackUrl (the surface's exact OAuth redirect URI).");

                    var surfaceDays = surface.RefreshTokenDays > 0 ? surface.RefreshTokenDays : 90;
                    var surfaceClient = new UserPoolClient($"{poolPrefix}-mcp-{surface.Name}-client", new UserPoolClientArgs
                    {
                        Name = $"{poolPrefix}-mcp-{surface.Name}-client",
                        UserPoolId = userPool.Id,
                        GenerateSecret = false,   // public ⇒ Cognito enforces PKCE (S256)
                        // ExplicitAuthFlows OMITTED (not empty-list) — same null-vs-empty drift rule as
                        // the base MCP client; this is an OAuth-only client.
                        SupportedIdentityProviders = { "COGNITO" },
                        PreventUserExistenceErrors = "ENABLED",
                        AllowedOauthFlows = { "code" },
                        AllowedOauthScopes = { "openid", "profile", "email", qualifiedMcpScope },
                        AllowedOauthFlowsUserPoolClient = true,
                        CallbackUrls = { surface.CallbackUrls.ToArray() },
                        LogoutUrls = { logoutUrls.ToArray() },
                        RefreshTokenValidity = surfaceDays,
                        AccessTokenValidity = 60,
                        IdTokenValidity = 60,
                        TokenValidityUnits = new UserPoolClientTokenValidityUnitsArgs
                        {
                            RefreshToken = "days",
                            AccessToken = "minutes",
                            IdToken = "minutes",
                        },
                        RefreshTokenRotation = new UserPoolClientRefreshTokenRotationArgs
                        {
                            Feature = "ENABLED",
                            RetryGracePeriodSeconds = 60,
                        },
                    }, new CustomResourceOptions
                    {
                        Parent = this,
                        DependsOn = { mcpResourceServer },
                        IgnoreChanges = { "explicitAuthFlows" },
                    });

                    mcpSurfaceClientIds.Add((surface.Name, surfaceClient.Id));
                }

                // =============================================================
                // MCP MACHINE CLIENTS (HostedMcpParity row 9) — headless
                // client_credentials principals granted the qualified MCP scope,
                // so automated verification (and future server-side agents) can
                // call the hosted /mcp without a browser. Same shape as the
                // MachineAuth M2M clients; no branding (no hosted UI), no
                // callbacks. Empty M2mClients ⇒ byte-identical baseline.
                // =============================================================
                foreach (var m2m in mcp.M2mClients)
                {
                    if (string.IsNullOrWhiteSpace(m2m.Name))
                        throw new InvalidOperationException(
                            $"Pool '{authType}' McpResource has an M2mClient with empty Name. Check systemconfig.");

                    var m2mMinutes = m2m.AccessTokenMinutes > 0 ? m2m.AccessTokenMinutes : 60;
                    _ = new UserPoolClient($"{poolPrefix}-mcp-m2m-{m2m.Name}", new UserPoolClientArgs
                    {
                        Name = $"{poolPrefix}-mcp-m2m-{m2m.Name}",
                        UserPoolId = userPool.Id,
                        GenerateSecret = true,
                        // client_credentials ONLY — no user-auth flow, no callback/logout URLs.
                        AllowedOauthFlows = { "client_credentials" },
                        AllowedOauthScopes = { qualifiedMcpScope },
                        AllowedOauthFlowsUserPoolClient = true,
                        AccessTokenValidity = m2mMinutes,
                        TokenValidityUnits = new UserPoolClientTokenValidityUnitsArgs
                        {
                            AccessToken = "minutes",
                        },
                    }, new CustomResourceOptions { Parent = this, DependsOn = { mcpResourceServer } });
                }
            }

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

            // ManagedLoginVersion=2 also requires a per-client branding for the
            // confidential BFF client. Without it the hosted UI returns
            // "Login pages unavailable" for /bff/login sign-ins (the public
            // client's branding does NOT cover a second client). Reuse the same
            // convention-folder branding source as the public client.
            if (poolConfig.ProvisionBffClient && bffClientId is not null)
            {
                var bffBrandingArgs = BuildBrandingArgsFromConventionFolder(
                    userPool.Id, bffClientId, authType);
                new ManagedLoginBranding($"{poolPrefix}-bff-branding", bffBrandingArgs,
                    new CustomResourceOptions
                    {
                        Parent = this,
                        DependsOn = { userPoolDomain },
                        DeleteBeforeReplace = true,
                    });
            }

            // ManagedLoginVersion=2 also requires a per-client branding for the
            // confidential Smartstore client. Without it the hosted UI returns
            // "Login pages unavailable" for storefront sign-ins (each client needs
            // its own branding slot). Reuse the same convention-folder source.
            if (poolConfig.ProvisionSmartstoreClient && smartstoreClientId is not null)
            {
                var ssBrandingArgs = BuildBrandingArgsFromConventionFolder(
                    userPool.Id, smartstoreClientId, authType);
                new ManagedLoginBranding($"{poolPrefix}-smartstore-branding", ssBrandingArgs,
                    new CustomResourceOptions
                    {
                        Parent = this,
                        DependsOn = { userPoolDomain },
                        DeleteBeforeReplace = true,
                    });
            }

            // ManagedLoginVersion=2 also requires a per-client branding for the
            // public MCP PKCE client. Without it the hosted UI returns "Login
            // pages unavailable" for the buyer/device agent's one-time PKCE
            // sign-in (each client needs its own branding slot — the public
            // app client's branding does NOT cover it). Gated on the same
            // McpResource opt-in that created the client, so the baseline stays
            // byte-identical for pools with no MCP endpoint.
            if (poolConfig.McpResource is not null && mcpClientId is not null)
            {
                var mcpBrandingArgs = BuildBrandingArgsFromConventionFolder(
                    userPool.Id, mcpClientId, authType);
                new ManagedLoginBranding($"{poolPrefix}-mcp-branding", mcpBrandingArgs,
                    new CustomResourceOptions
                    {
                        Parent = this,
                        DependsOn = { userPoolDomain },
                        DeleteBeforeReplace = true,
                    });
            }

            // …and for EACH per-surface connector client (BuyerOnboarding.md #2/#9): every hosted-UI
            // client needs its own branding slot under ManagedLoginVersion=2, or that surface's
            // "Connect" sign-in shows "Login pages unavailable" — a runtime failure the deploy does
            // not catch. Gated on the same SurfaceClients opt-in that created the clients.
            foreach (var (surfaceName, surfaceClientId) in mcpSurfaceClientIds)
            {
                var surfaceBrandingArgs = BuildBrandingArgsFromConventionFolder(
                    userPool.Id, surfaceClientId, authType);
                new ManagedLoginBranding($"{poolPrefix}-mcp-{surfaceName}-branding", surfaceBrandingArgs,
                    new CustomResourceOptions
                    {
                        Parent = this,
                        DependsOn = { userPoolDomain },
                        DeleteBeforeReplace = true,
                    });
            }

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
                // BFF confidential-client outputs. Null unless ProvisionBffClient
                // was set for this pool — downstream secret plumbing skips pools
                // without a BFF client.
                BffClientId = bffClientId,
                BffClientSecret = bffClientSecret,
                // Smartstore confidential-client outputs. Null unless
                // ProvisionSmartstoreClient was set for this pool.
                SmartstoreClientId = smartstoreClientId,
                SmartstoreClientSecret = smartstoreClientSecret,
            };
        }

        return new AwsCognitoOutputs
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

    /// <summary>
    /// Confidential BFF client id. Non-null only when the pool's
    /// <c>ProvisionBffClient</c> flag was set; <c>null</c> otherwise (no BFF
    /// client was created). See <c>MultiTenantAuth.md §8.5</c>.
    /// </summary>
    public Output<string>? BffClientId { get; init; }

    /// <summary>
    /// Confidential BFF client secret. Non-null only when the pool's
    /// <c>ProvisionBffClient</c> flag was set. Persisted into the tenant
    /// Secrets Manager secret by the tenant-data flow, never exported in clear
    /// to the SPA.
    /// </summary>
    public Output<string>? BffClientSecret { get; init; }

    /// <summary>
    /// Confidential Smartstore client id. Non-null only when the pool's
    /// <c>ProvisionSmartstoreClient</c> flag was set; <c>null</c> otherwise.
    /// </summary>
    public Output<string>? SmartstoreClientId { get; init; }

    /// <summary>
    /// Confidential Smartstore client secret. Non-null only when the pool's
    /// <c>ProvisionSmartstoreClient</c> flag was set. Consumed by the storefront
    /// container as <c>SMARTSTORE_COGNITO_CLIENTSECRET</c>.
    /// </summary>
    public Output<string>? SmartstoreClientSecret { get; init; }
}

/// <summary>
/// Cognito service outputs implementing IAuthPoolOutputs.
/// </summary>
public class AwsCognitoOutputs : IAuthPoolOutputs
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
                BffClientId = kv.Value.BffClientId,
                BffClientSecret = kv.Value.BffClientSecret,
                SmartstoreClientId = kv.Value.SmartstoreClientId,
                SmartstoreClientSecret = kv.Value.SmartstoreClientSecret,
            });
}
