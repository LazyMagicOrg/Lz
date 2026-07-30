using Lz.Aws.Verification;

namespace Lz.Tests.Verification.Tests;

/// <summary>
/// Pure decisions behind the `lz verify` runtime-smoke checks (E2ETestPlan
/// P0.7): the /config drift detector's pool validation. The HTTP/Lambda probes
/// stay thin — pinning the JSON judgment pins the drift detection.
/// </summary>
public class SmokeProbeTests
{
    private static readonly string[] AllPools = { "tenantauth", "consumerauth", "systemauth" };

    [Fact]
    public void AllPoolsWithClientIds_NothingMissing()
    {
        const string json = """
            {"authConfigs":{
              "tenantauth":{"ClientId":"abc","Authority":"https://x/auth/tenantauth"},
              "consumerauth":{"ClientId":"def"},
              "systemauth":{"ClientId":"ghi"}}}
            """;
        Assert.Empty(AwsLiveVerifier.MissingPoolClientIds(json, AllPools));
    }

    [Fact]
    public void MissingPool_IsReported()
    {
        const string json = """
            {"authConfigs":{"tenantauth":{"ClientId":"abc"},"consumerauth":{"ClientId":"def"}}}
            """;
        Assert.Equal(new[] { "systemauth" },
            AwsLiveVerifier.MissingPoolClientIds(json, AllPools));
    }

    [Fact]
    public void BlankClientId_CountsAsMissing()
    {
        // The exact drift a pool recreate produces: the entry survives in KVS
        // with an empty ClientId. Must NOT pass.
        const string json = """
            {"authConfigs":{
              "tenantauth":{"ClientId":""},
              "consumerauth":{"ClientId":"def"},
              "systemauth":{"ClientId":"ghi"}}}
            """;
        Assert.Equal(new[] { "tenantauth" },
            AwsLiveVerifier.MissingPoolClientIds(json, AllPools));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("""{"authConfigs":[]}""")]
    // Valid-JSON non-object roots: must CLASSIFY (total contract), not throw
    // InvalidOperationException out of TryGetProperty.
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"a string\"")]
    [InlineData("42")]
    public void UnusableDocument_EveryPoolMissing(string json)
        => Assert.Equal(AllPools, AwsLiveVerifier.MissingPoolClientIds(json, AllPools));

    [Fact]
    public void NonStringClientId_CountsAsMissing_NotThrow()
    {
        // Corrupted KVS survives CFAuthConfig's `|| ""` fallback (JS truthiness
        // keeps numbers/objects) — classify as drift, don't crash the probe.
        const string json = """
            {"authConfigs":{
              "tenantauth":{"ClientId":42},
              "consumerauth":{"ClientId":"def"},
              "systemauth":{"ClientId":"ghi"}}}
            """;
        Assert.Equal(new[] { "tenantauth" },
            AwsLiveVerifier.MissingPoolClientIds(json, AllPools));
    }

    [Fact]
    public void NoDeclaredPools_TriviallyPasses()
        => Assert.Empty(AwsLiveVerifier.MissingPoolClientIds("{}", Array.Empty<string>()));
}

/// <summary>
/// The api-health classification: a bare 200 through the CDN proves nothing —
/// the distribution rewrites origin 403/404 to a 200 SPA shell, and /health is
/// exempt from origin-verify rejection. Only the middleware's x-origin-verified
/// echo proves the edge→origin secret matches on the live path.
/// </summary>
public class ApiHealthClassifierTests
{
    [Fact]
    public void Verified200_IsPresent()
        => Assert.Equal(ResourceState.Present,
            AwsLiveVerifier.ClassifyApiHealth(200, "true", "application/json").State);

    [Fact]
    public void SecretDrift_FalseAnnotation_Fails()
    {
        var (state, detail) = AwsLiveVerifier.ClassifyApiHealth(200, "false", "text/plain");
        Assert.Equal(ResourceState.Absent, state);
        Assert.Contains("drift", detail);
    }

    [Fact]
    public void SpaShellRewrite_200Html_Fails()
    {
        // The exact mask: origin 403/404 rewritten to 200 /index.html.
        var (state, detail) = AwsLiveVerifier.ClassifyApiHealth(200, null, "text/html");
        Assert.Equal(ResourceState.Absent, state);
        Assert.Contains("SPA rewrite", detail);
    }

    [Fact]
    public void Bare200WithoutAnnotation_Fails()
        => Assert.Equal(ResourceState.Absent,
            AwsLiveVerifier.ClassifyApiHealth(200, null, "text/plain").State);

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(403)]
    public void NonSuccess_Fails(int status)
        => Assert.Equal(ResourceState.Absent,
            AwsLiveVerifier.ClassifyApiHealth(status, null, null).State);
}

/// <summary>
/// The function-url-lockout classification — the tripwire that caught the live
/// publicly-invokable-URL hole. Only the middleware's marker 403 proves the gate;
/// widening this (e.g. to any 403, or "404 means locked") silently re-opens it.
/// </summary>
public class FunctionUrlLockoutClassifierTests
{
    [Fact]
    public void Marker403_IsPresent()
        => Assert.Equal(ResourceState.Present,
            AwsLiveVerifier.ClassifyFunctionUrlLockout(
                403, """{"error":"origin_verification_failed"}""").State);

    [Fact]
    public void Bare403_ServicePermission403_IsNotTheGate()
    {
        // The Lambda service's own 403 (public invoke grant missing): API down,
        // gate unproven — must NOT attest the lockout.
        var (state, detail) = AwsLiveVerifier.ClassifyFunctionUrlLockout(403, "Forbidden");
        Assert.Equal(ResourceState.Absent, state);
        Assert.Contains("permission", detail);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(404)] // the live incident: app booted without the gate, "/" → 404
    [InlineData(500)]
    public void AnythingElse_IsPubliclyInvokable(int status)
    {
        var (state, detail) = AwsLiveVerifier.ClassifyFunctionUrlLockout(status, "");
        Assert.Equal(ResourceState.Absent, state);
        Assert.Contains("PUBLICLY invokable", detail);
    }
}

/// <summary>Unauthenticated /bff/user must 401 (401 is not in the CDN's 403/404→200 rewrite set).</summary>
public class BffUserLockoutClassifierTests
{
    [Fact]
    public void Unauthenticated401_IsPresent()
        => Assert.Equal(ResourceState.Present,
            AwsLiveVerifier.ClassifyBffUserLockout(401, "application/json").State);

    [Fact]
    public void SpaShell_MeansBffUnrouted()
    {
        var (state, detail) = AwsLiveVerifier.ClassifyBffUserLockout(200, "text/html");
        Assert.Equal(ResourceState.Absent, state);
        Assert.Contains("not routed", detail);
    }

    [Theory]
    [InlineData(200)] // 200 json without auth would be a broken controller
    [InlineData(403)]
    [InlineData(502)]
    public void AnythingElse_Fails(int status)
        => Assert.Equal(ResourceState.Absent,
            AwsLiveVerifier.ClassifyBffUserLockout(status, "application/json").State);
}

/// <summary>
/// RunSmoke's exception→Absent mapping is load-bearing: post-destroy every probe
/// throws (unreachable surfaces), and Absent is the expected teardown state.
/// Error instead would poison `--expect destroyed` via the errors-downgrade rule.
/// Do NOT harmonize with VerifyContext.Run (which maps exceptions to Error).
/// </summary>
public class RunSmokeTests
{
    [Fact]
    public async Task ThrowingProbe_MapsToAbsent_NeverError()
    {
        var result = await AwsLiveVerifier.RunSmoke("edge", "api-health", "x",
            () => throw new HttpRequestException("connection refused"));
        Assert.Equal(ResourceCategory.Smoke, result.Category);
        Assert.Equal(ResourceState.Absent, result.State);
        Assert.NotEqual(ResourceState.Error, result.State);
        Assert.Contains("HttpRequestException", result.Detail);
    }

    [Fact]
    public async Task HealthyProbe_PassesThrough()
    {
        var result = await AwsLiveVerifier.RunSmoke("edge", "config-bootstrap", "x",
            () => Task.FromResult((ResourceState.Present, (string?)"ok")));
        Assert.Equal(ResourceState.Present, result.State);
        Assert.Equal("ok", result.Detail);
    }
}

/// <summary>
/// The --expect verdict rules. The one that must never regress: smoke probes are
/// IN the 'deployed' verdict (surfaces misbehaving ≠ deployed) and OUT of the
/// 'destroyed' verdict (smoke cannot veto a teardown).
/// </summary>
public class VerifyVerdictTests
{
    private static ResourceCheckResult R(ResourceCategory cat, ResourceState state)
        => new(cat, "svc", "kind", "name", state);

    private static readonly ResourceCheckResult StackUp = R(ResourceCategory.Stack, ResourceState.Present);
    private static readonly ResourceCheckResult SmokeUp = R(ResourceCategory.Smoke, ResourceState.Present);
    private static readonly ResourceCheckResult SmokeDown = R(ResourceCategory.Smoke, ResourceState.Absent);

    [Fact]
    public void Deployed_AllStackAndSmokePresent_Met()
        => Assert.True(VerifyVerdict.Compute("deployed",
            new[] { StackUp, SmokeUp, R(ResourceCategory.Persistent, ResourceState.Absent) }));

    [Fact]
    public void Deployed_FailingSmokeProbe_FlipsToNotMet()
        => Assert.False(VerifyVerdict.Compute("deployed", new[] { StackUp, SmokeDown }));

    [Fact]
    public void Deployed_TombstonedStackSecret_NotMet()
        => Assert.False(VerifyVerdict.Compute("deployed",
            new[] { R(ResourceCategory.Stack, ResourceState.ScheduledForDeletion), SmokeUp }));

    [Fact]
    public void Destroyed_IgnoresSmokeState()
        => Assert.True(VerifyVerdict.Compute("destroyed",
            new[] { R(ResourceCategory.Stack, ResourceState.Absent), SmokeDown, SmokeUp }));

    [Fact]
    public void Destroyed_LingeringStack_NotMet()
        => Assert.False(VerifyVerdict.Compute("destroyed", new[] { StackUp, SmokeDown }));

    [Fact]
    public void Error_DowngradesMetToNotMet()
        => Assert.False(VerifyVerdict.Compute("deployed",
            new[] { StackUp, SmokeUp, R(ResourceCategory.Persistent, ResourceState.Error) }));

    [Fact]
    public void NoExpectation_IsNull()
        => Assert.Null(VerifyVerdict.Compute(null, new[] { StackUp }));
}
