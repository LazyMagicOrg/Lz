using Lz.Core.Config;

namespace Lz.Aws.Config;

/// <summary>
/// AWS-specific <see cref="AuthConfigEntry"/> carrying Cognito user-pool
/// policy fields (MFA, password policy, advanced security, groups, dev
/// callback URLs). Materialised automatically when the active platform is
/// <c>aws</c> via the <c>WithTypeMapping</c> in
/// <see cref="AwsConfigExtensions"/>; consumers in the AWS path cast with
/// <c>if (entry is AwsAuthConfigEntry aws)</c> to read the extras.
/// </summary>
/// <remarks>
/// Defaults are chosen to preserve current behaviour on upgrade — nothing
/// here is required in YAML, so existing configs keep working unchanged.
/// See <c>BCProjNew/Platform/CognitoHardeningPlan.md</c> for the field
/// semantics and the consuming component work.
/// </remarks>
public class AwsAuthConfigEntry : AuthConfigEntry
{
    /// <summary>
    /// Pool-level MFA enforcement. One of <c>OFF</c> | <c>ON</c> |
    /// <c>OPTIONAL</c>. Default <c>OFF</c>. Note: the Pulumi/Terraform AWS
    /// provider uses <c>ON</c> where the AWS-SDK-native enum reads
    /// <c>REQUIRED</c>; always write <c>ON</c> in YAML.
    /// </summary>
    public string MfaConfiguration { get; set; } = "OFF";

    /// <summary>
    /// Enable software TOTP MFA (authenticator apps). Required when
    /// <see cref="MfaConfiguration"/> is not <c>OFF</c> unless SMS is used.
    /// </summary>
    public bool SoftwareTokenMfa { get; set; } = false;

    /// <summary>
    /// Enable SMS MFA. Requires an SNS role attached to the pool.
    /// </summary>
    public bool SmsMfa { get; set; } = false;

    /// <summary>
    /// Minimum password length enforced by the pool's password policy.
    /// </summary>
    public int PasswordMinLength { get; set; } = 8;

    /// <summary>
    /// Whether the password policy requires at least one symbol.
    /// </summary>
    public bool PasswordRequireSymbols { get; set; } = false;

    /// <summary>
    /// Advanced Security mode (UserPoolAddOns). One of <c>OFF</c> |
    /// <c>AUDIT</c> | <c>ENFORCED</c>. <c>AUDIT</c> logs risk events;
    /// <c>ENFORCED</c> enables risk-based auth.
    /// </summary>
    public string AdvancedSecurityMode { get; set; } = "OFF";

    /// <summary>
    /// Cognito groups to create inside this pool. Surfaced in JWTs as the
    /// <c>cognito:groups</c> claim.
    /// </summary>
    public List<CognitoGroup>? Groups { get; set; }

    /// <summary>
    /// When true, the pool client's callback/logout URL lists include dev
    /// entries (localhost, MAUI custom URI scheme). Dev/test envs set this;
    /// prod leaves it false so prod clients reject non-prod redirects.
    /// </summary>
    public bool IncludeDevCallbackUrls { get; set; } = false;

    /// <summary>
    /// When true, end users can self-register via Cognito Hosted UI's
    /// "Sign up" link. Translates to <c>AllowAdminCreateUserOnly = false</c>
    /// on the user pool's <c>AdminCreateUserConfig</c>. Default <c>false</c>
    /// — matches the historical lockdown where admins create all users.
    /// Typically enabled only on planner / consumer pools; admin /
    /// operator pools stay admin-only.
    /// </summary>
    public bool AllowSelfSignUp { get; set; } = false;

    /// <summary>
    /// Cognito User Pool device tracking. One of <c>OFF</c> | <c>ALWAYS</c> |
    /// <c>USER_OPT_IN</c>. Default <c>USER_OPT_IN</c>.
    /// <list type="bullet">
    ///   <item><description><c>OFF</c> — no device tracking. No
    ///     <c>DeviceConfiguration</c> on the pool. Device key is not
    ///     captured; refresh tokens are not device-bound; threat-protection
    ///     logs aren't enriched with per-device identity.</description></item>
    ///   <item><description><c>USER_OPT_IN</c> — track devices when the
    ///     user opts in. Hosted UI shows a "Remember this device?" checkbox
    ///     in the MFA flow. <strong>If MFA is OFF on the pool, the checkbox
    ///     never appears and no device is ever remembered</strong> — use
    ///     <c>ALWAYS</c> for non-MFA pools that want device binding.
    ///     Maps to <c>DeviceOnlyRememberedOnUserPrompt = true</c>.</description></item>
    ///   <item><description><c>ALWAYS</c> — auto-remember every device on
    ///     first sign-in. Required to get device-bound refresh tokens on
    ///     pools where MFA is OFF. Maps to
    ///     <c>DeviceOnlyRememberedOnUserPrompt = false</c>.</description></item>
    /// </list>
    /// Both non-OFF modes set <c>ChallengeRequiredOnNewDevice = true</c>;
    /// when MFA is OFF, that flag is a no-op for the user-visible flow but
    /// is what enables device-key capture and refresh-token binding.
    /// Device key cookie is stored on the Cognito custom domain
    /// (<c>auth-{pool}.{rootdomain}</c>), so device trust is per-pool, not
    /// per-subtenant — a user trusted at <c>auth-planner</c> once is
    /// trusted across every subtenant that uses plannerauth.
    /// </summary>
    public string DeviceTracking { get; set; } = "USER_OPT_IN";

    /// <summary>
    /// When true, provision a SECOND, confidential Cognito app client
    /// (<c>{poolPrefix}-bff-client</c>, <c>GenerateSecret=true</c>) alongside
    /// the existing public client, for the Backend-For-Frontend (BFF) auth
    /// flow. The public client is NOT modified — the BFF client is purely
    /// additive. Default <c>false</c>: when unset, no extra client, secret,
    /// or outputs are created, so the deploy plan is byte-for-byte identical
    /// to a pre-BFF deploy. See <c>Platform/MultiTenantAuth.md §8.5</c>.
    /// </summary>
    public bool ProvisionBffClient { get; set; } = false;

    /// <summary>
    /// Per-pool BFF session lifetime, in hours. Drives the confidential
    /// client's explicit <c>RefreshTokenValidity</c> (and the DynamoDB
    /// session-record TTL the BFF server enforces). Per
    /// <c>MultiTenantAuth.md §8.14</c>: employee pools (tenantauth/admin)
    /// ≈ 12 h absolute; consumer pools run much longer (e.g. 720 h ≈ 30 d).
    /// Only consulted when <see cref="ProvisionBffClient"/> is true; default
    /// <c>12</c> matches the employee-pool recommendation.
    /// </summary>
    public int BffSessionTtlHours { get; set; } = 12;

    /// <summary>
    /// BFF route prefix this pool's confidential client serves at — drives the
    /// registered callback/logout URLs (<c>{prefix}/callback</c>,
    /// <c>{prefix}/logout-callback</c>). Default <c>/bff</c> (tenantauth). A second
    /// pool sharing the same apphost (e.g. consumerauth) MUST use a distinct prefix
    /// (e.g. <c>/cbff</c>) so the two BFF instances' endpoints/cookies never collide.
    /// Only consulted when <see cref="ProvisionBffClient"/> is true.
    /// </summary>
    public string BffRoutePrefix { get; set; } = "/bff";

    /// <summary>
    /// Opt-in OAuth2 <c>client_credentials</c> (machine-to-machine) provisioning for this pool.
    /// When null (the default) NO Cognito resource server, custom scopes, or M2M app clients are
    /// created — the deploy plan is byte-for-byte identical to before, so existing systems/pools are
    /// unaffected (same guarantee as <see cref="ProvisionBffClient"/>). Cannot be bolted onto the
    /// public or BFF client: Cognito forbids mixing <c>client_credentials</c> with the code flow on
    /// one client, so each machine client is a NEW confidential app client. See McpAgents.md M0-2.
    /// </summary>
    public MachineAuthConfig? MachineAuth { get; set; }

    /// <summary>
    /// Opt-in USER-principal credential provisioning for this pool (the M0-7 seller Custom-Auth + buyer
    /// PKCE model). When null (the default) NO Custom-Auth Lambda, vendor-credential table, pool
    /// <c>LambdaConfig</c>, or seller/buyer client is created — the deploy plan is byte-for-byte identical to
    /// before, so existing systems/pools are unaffected (same guarantee as <see cref="MachineAuth"/>). This is
    /// DISTINCT from <see cref="MachineAuth"/>: that is app-only <c>client_credentials</c>; this authenticates
    /// per-vendor Cognito USERS non-interactively (Custom-Auth challenge over a vendor API key). Because the
    /// <c>LambdaConfig</c> it wires is POOL-LEVEL, every resource here — the Lambda especially — MUST stay
    /// inside the opt-in guard. See McpAgents.md M0-7 and Lz.Aws/AppRunner/CognitoCustomAuth/custom-auth.mjs.
    /// </summary>
    public CustomAuthConfig? CustomAuth { get; set; }

    /// <summary>
    /// When true, provision a confidential Cognito app client
    /// (<c>{poolPrefix}-smartstore-client</c>, <c>GenerateSecret=true</c>) for the
    /// Smartstore storefront's OpenID Connect handler (the
    /// <c>Smartstore.Cognito.Auth</c> module). The client's callback is the store
    /// apex (<c>https://{systemDomain}/signin-cognito</c>) and its sign-out URL is
    /// the apex root (<c>https://{systemDomain}/</c>). Like <see cref="ProvisionBffClient"/>
    /// this is purely additive: the public client is untouched, and when the flag is
    /// off (the default) no extra client, secret, branding, or outputs are created,
    /// so the deploy plan is byte-for-byte identical. Provision this on the pool the
    /// storefront authenticates against (typically <c>consumerauth</c>).
    /// See <c>Platform/SMARTSTORE-COGNITO-AUTH.md §8</c>.
    /// </summary>
    public bool ProvisionSmartstoreClient { get; set; } = false;
}

/// <summary>
/// Machine-to-machine (<c>client_credentials</c>) provisioning for a pool: one Cognito resource
/// server declaring custom scopes, plus a roster of confidential app clients that each request a
/// subset of those scopes. Purely additive — see <see cref="AwsAuthConfigEntry.MachineAuth"/>.
/// </summary>
public class MachineAuthConfig
{
    /// <summary>
    /// Resource-server identifier — the audience prefix Cognito prepends to each scope on an issued
    /// access token (e.g. <c>https://api.aiproxydev.click/scutara</c>). Not a real URL that must
    /// resolve; it just namespaces the scopes. Required when any <see cref="Clients"/> are declared.
    /// </summary>
    public string Identifier { get; set; } = string.Empty;

    /// <summary>Custom scopes to declare on the resource server (surfaced in the <c>scope</c> claim).</summary>
    public List<MachineScope> Scopes { get; set; } = new();

    /// <summary>The confidential <c>client_credentials</c> app clients to provision.</summary>
    public List<MachineClientConfig> Clients { get; set; } = new();

    /// <summary>Access-token lifetime, in minutes, for the machine clients. Default <c>60</c>.</summary>
    public int AccessTokenMinutes { get; set; } = 60;
}

/// <summary>A custom scope on the resource server, e.g. <c>seller</c> / <c>buyer</c>.</summary>
public class MachineScope
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
}

/// <summary>
/// One machine app client. <see cref="Scopes"/> are the bare scope names (e.g. <c>seller</c>); the
/// component qualifies them as <c>{Identifier}/{scope}</c> for the client's allowed-scope list.
/// </summary>
public class MachineClientConfig
{
    public string Name { get; set; } = string.Empty;
    public List<string> Scopes { get; set; } = new();
}

/// <summary>
/// USER-principal credential provisioning for a pool (M0-7). Present ⇒ the component provisions: a DynamoDB
/// table of per-vendor API-key hashes, the Custom-Auth challenge Lambda (from
/// <c>CognitoCustomAuth/custom-auth.mjs</c>) with its IAM role + Cognito invoke permission, the pool's
/// <c>LambdaConfig</c> (Define/Create/Verify → that Lambda), and a confidential SELLER app client
/// (<c>ALLOW_CUSTOM_AUTH</c> + the <c>ALLOW_ADMIN_USER_PASSWORD_AUTH</c> MVP fallback). Optionally a PUBLIC
/// buyer/device client (Auth Code + PKCE + refresh-token rotation). Purely additive — see
/// <see cref="AwsAuthConfigEntry.CustomAuth"/>.
/// </summary>
public class CustomAuthConfig
{
    /// <summary>Name suffix of the confidential seller client (<c>{poolPrefix}-{SellerClientName}-client</c>).</summary>
    public string SellerClientName { get; set; } = "seller-agent";

    /// <summary>Access-token lifetime, in minutes, for the seller client. Default <c>60</c>.</summary>
    public int AccessTokenMinutes { get; set; } = 60;

    /// <summary>
    /// When set, ALSO provision a PUBLIC buyer/device client (<c>{poolPrefix}-{BuyerDeviceClientName}-client</c>,
    /// <c>GenerateSecret=false</c>) — Auth Code + PKCE (Cognito enforces PKCE for public clients) with
    /// refresh-token ROTATION enabled. When null, no buyer client is created.
    /// </summary>
    public string? BuyerDeviceClientName { get; set; }

    /// <summary>Buyer refresh-token validity, in days (rotation on). Default <c>90</c>. Only consulted when
    /// <see cref="BuyerDeviceClientName"/> is set.</summary>
    public int BuyerRefreshTokenDays { get; set; } = 90;
}

/// <summary>
/// A Cognito user-pool group. Groups carry finer-grained role distinctions
/// within a pool without affecting pool-level settings like MFA.
/// </summary>
public class CognitoGroup
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int Precedence { get; set; } = 10;

    /// <summary>
    /// Optional IAM role ARN. Used by Cognito Identity Pool role-mapping.
    /// </summary>
    public string? RoleArn { get; set; }
}
