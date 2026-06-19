namespace Lz.Core.Config;

/// <summary>
/// CDN deployment configuration section — shared between systemconfig and tenantconfig.
/// Maps to the "CDN:" section in YAML.
/// </summary>
public class CdnConfig
{
    public string PriceClass { get; set; } = "PriceClass_100";
    public string DefaultRootObject { get; set; } = "app/index.html";

    /// <summary>
    /// Cross-origin resource sharing rules baked into the viewer-response
    /// CloudFront Function (CFResponse.js) at deploy time. Null/omitted →
    /// no Access-Control-Allow-Origin headers echoed (production-safe
    /// default — the WASM app is always same-origin with its assets in a
    /// well-formed deployment, so cross-origin echoing is purely a dev
    /// convenience).
    /// </summary>
    public CorsConfig? Cors { get; set; }
}

/// <summary>
/// Operator-controlled allowlist for which browser Origins this CDN
/// distribution will echo back as <c>Access-Control-Allow-Origin</c> on
/// asset/API responses. Both fields default to null/empty; with no Cors
/// block at all, the distribution echoes nothing — the right default for
/// prod where the WASM app is always served from the same origin as its
/// assets.
/// <para>
/// Typical pattern: declare in <c>tenantconfig.{sk}.{tk}.{env}.yaml</c>
/// per-environment so dev/test can opt in to localhost dev-loop access
/// without pulling that capability into prod:
/// <code>
/// CDN:
///   Cors:
///     AllowLocalhostDev: true                # dev/test only
///     AllowedOrigins:
///       - "https://staging.example.com"      # exact-match origins
/// </code>
/// </para>
/// <para>
/// The values are baked into <c>CFResponse.js</c> at deploy time via
/// string substitution. Changing them requires a re-deploy — no live KVS
/// lookup per response, no per-request latency. The CFResponse function
/// runs after CloudFront's cache lookup, so cached bodies still get
/// fresh per-request CORS headers based on the originating Origin.
/// </para>
/// </summary>
public class CorsConfig
{
    /// <summary>
    /// Convenience flag for the common dev-loop case: when true, the
    /// distribution echoes <c>Access-Control-Allow-Origin</c> for any
    /// origin matching <c>^https?://localhost(:\d+)?$</c>. Use on dev
    /// (and usually test) so VS-hosted local WASM apps can fetch cloud
    /// assets without a CORS error. Leave false on prod — real
    /// browsers never have <c>localhost</c> as their Origin in the
    /// wild, but explicitly disabling avoids advertising anything in
    /// the response headers that doesn't need to be there.
    /// </summary>
    public bool AllowLocalhostDev { get; set; } = false;

    /// <summary>
    /// Exact-match origin allowlist. Each entry is the full origin
    /// string the browser sends in the <c>Origin</c> header — no
    /// wildcards, no port flexibility. Use for fixed staging hosts,
    /// partner integration sandboxes, or trusted external dashboards.
    /// </summary>
    public List<string>? AllowedOrigins { get; set; }
}
