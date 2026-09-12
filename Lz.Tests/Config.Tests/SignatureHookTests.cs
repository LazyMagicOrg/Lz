using System.Text;
using System.Text.Json;
using Lz.Aws.Pipeline;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// The ECS <c>PRE_SCALE_UP</c> signature hook's decisions (DecoupledCd.md §4.4.1, punchlist P2 C4).
///
/// <para>THE ASYMMETRY THESE TESTS GUARD: a hook that wrongly answers FAILED stops a deploy, loudly,
/// and is fixed the same hour; a hook that wrongly answers SUCCEEDED admits an image nobody verified,
/// silently, and is found — if ever — by someone reading logs after an incident. So nearly every test
/// here is a way to reach SUCCEEDED that must not work.</para>
/// </summary>
public class SignatureHookTests
{
    private const string Registry = "503947800380.dkr.ecr.us-west-2.amazonaws.com";
    private const string Repo = Registry + "/scu-4df6-b9c6-aiphost";
    private const string Digest = "sha256:5e4f504e5ba9a0d90d25f1f111da78e1ce16ca5b40410332fb242a192a4f9e95";
    private const string Image = Repo + "@" + Digest;
    private const string Profile = "arn:aws:signer:us-west-2:147440642635:/signing-profiles/scu_build_ci_scutaraservice";

    private static readonly string[] Scopes = { Repo };

    // ---------------------------------------------------------------------------------------
    //  The trust policy
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void TheTrustPolicy_IsStrict_AndTrustsExactlyTheNamedProfiles()
    {
        using var doc = JsonDocument.Parse(SignatureHook.TrustPolicy(new[] { Profile }, Scopes));
        var root = doc.RootElement;
        var policy = Assert.Single(root.GetProperty("trustPolicies").EnumerateArray());

        // notation-go supports exactly one policy version.
        Assert.Equal("1.0", root.GetProperty("version").GetString());
        Assert.Equal("strict", policy.GetProperty("signatureVerification").GetProperty("level").GetString());
        Assert.Equal(new[] { "signingAuthority:aws-signer-ts" },
            policy.GetProperty("trustStores").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(new[] { Profile },
            policy.GetProperty("trustedIdentities").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(Scopes, policy.GetProperty("registryScopes").EnumerateArray().Select(x => x.GetString()));
    }

    [Fact]
    public void NoTrustedIdentities_IsRefused()
    {
        Assert.Throws<InvalidOperationException>(() => SignatureHook.TrustPolicy(Array.Empty<string>(), Scopes));
    }

    [Theory]
    [InlineData("*")]
    [InlineData("arn:aws:signer:us-west-2:147440642635:/signing-profiles/*")]
    public void AWildcardIdentity_IsRefused(string identity)
    {
        // A signature under a profile nobody named proves only that someone, somewhere, signed it.
        var ex = Assert.Throws<InvalidOperationException>(() => SignatureHook.TrustPolicy(new[] { identity }, Scopes));
        Assert.Contains("wildcard", ex.Message);
    }

    [Theory]
    [InlineData("arn:aws:iam::147440642635:role/scu-build-ci-scutaraservice")]
    [InlineData("scu_build_ci_scutaraservice")]
    public void AnIdentityThatIsNotASigningProfile_IsRefused(string identity)
    {
        Assert.Throws<InvalidOperationException>(() => SignatureHook.TrustPolicy(new[] { identity }, Scopes));
    }

    [Theory]
    [InlineData("*")]                                         // governs everything — the thing scoping exists to avoid
    [InlineData(Registry + "/scu-*")]
    [InlineData(Repo + ":latest")]                            // a tag
    [InlineData(Repo + "@" + Digest)]                         // a digest
    [InlineData("https://" + Repo)]                           // a scheme
    [InlineData("scu-4df6-b9c6-aiphost")]                     // not fully qualified
    [InlineData("")]
    public void AScopeNotationWouldReject_IsRefusedAtPlanTime(string scope)
    {
        // Notation's own rule: "a fully qualified repository without the scheme, protocol or tag".
        // Refused here so the failure is a plan-time message rather than every deploy dying at the hook.
        Assert.Throws<InvalidOperationException>(() => SignatureHook.ValidateScopes(new[] { scope }));
    }

    [Fact]
    public void NoScopes_IsRefused()
    {
        Assert.Throws<InvalidOperationException>(() => SignatureHook.ValidateScopes(Array.Empty<string>()));
    }

    [Fact]
    public void AScopeWithARegistryPort_IsAccepted()
    {
        // The tag check must look after the LAST slash; a colon in the registry host is not a tag.
        SignatureHook.ValidateScopes(new[] { "localhost:5000/repo" });
    }

    // ---------------------------------------------------------------------------------------
    //  Which images
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void ADigestReferenceInScope_IsAdmittedForVerification()
    {
        var image = Assert.Single(SignatureHook.ImagesFrom(new[] { Image }, Scopes));

        Assert.Null(image.Refusal);
        Assert.Equal(Image, image.DigestReference);
    }

    [Theory]
    [InlineData(Repo + ":latest")]
    [InlineData(Repo)]
    public void ATag_IsRefused_NotResolved(string raw)
    {
        // The hook would verify what the tag points at now; ECS would pull what it points at later.
        var image = Assert.Single(SignatureHook.ImagesFrom(new[] { raw }, Scopes));

        Assert.Null(image.DigestReference);
        Assert.Contains("TAG", image.Refusal);
    }

    [Theory]
    [InlineData(Repo + "@sha256:5E4F504E5BA9A0D90D25F1F111DA78E1CE16CA5B40410332FB242A192A4F9E95")] // uppercase
    [InlineData(Repo + "@sha256:5e4f504e")]                                                           // short
    [InlineData(Repo + "@sha256:" + "5e4f504e5ba9a0d90d25f1f111da78e1ce16ca5b40410332fb242a192a4f9e9500")] // long
    public void AMalformedDigest_IsRefused(string raw)
    {
        var image = Assert.Single(SignatureHook.ImagesFrom(new[] { raw }, Scopes));

        Assert.Contains("malformed digest", image.Refusal);
    }

    [Fact]
    public void AnImageOutsideTheScopes_IsRefusedBeforeNotationSeesIt()
    {
        // Correctly signed or not, an image from a repository the policy does not govern is not admitted.
        var image = Assert.Single(SignatureHook.ImagesFrom(
            new[] { "147440642635.dkr.ecr.us-west-2.amazonaws.com/scu-4df6-b9c6-aiphost@" + Digest }, Scopes));

        Assert.Contains("does not govern", image.Refusal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void AContainerWithNoImage_IsRefused(string? raw)
    {
        Assert.NotNull(Assert.Single(SignatureHook.ImagesFrom(new[] { raw }, Scopes)).Refusal);
    }

    [Fact]
    public void NullContainerListFromTheSdk_IsNoImages_NotACrash()
    {
        Assert.Empty(SignatureHook.ImagesFrom(null, Scopes));
    }

    // ---------------------------------------------------------------------------------------
    //  Reading Notation's answer
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void ExitZeroAndTheSuccessLineForThisReference_IsVerified()
    {
        // notation v1.3.2: fmt.Println("Successfully verified signature for", printout).
        var v = SignatureHook.InterpretNotation(Image, 0, $"Successfully verified signature for {Image}\n", "");

        Assert.True(v.Verified);
    }

    [Fact]
    public void ACarriageReturnLineEnding_IsStillTheSameLine()
    {
        Assert.True(SignatureHook.InterpretNotation(Image, 0, $"Successfully verified signature for {Image}\r\n", "").Verified);
    }

    [Fact]
    public void ExitZeroWithoutTheSuccessLine_IsNotVerified()
    {
        // A wrapper, a changed format, a truncated stream: none of them is a pass.
        Assert.False(SignatureHook.InterpretNotation(Image, 0, "", "").Verified);
    }

    [Fact]
    public void TheSuccessLineForADifferentImage_IsNotVerified()
    {
        var other = Repo + "@sha256:" + new string('a', 64);

        Assert.False(SignatureHook.InterpretNotation(Image, 0, $"Successfully verified signature for {other}\n", "").Verified);
    }

    [Fact]
    public void TheSuccessLineWithANonZeroExit_IsNotVerified()
    {
        // Both signals must agree. An exit code says it failed; believe the failure.
        Assert.False(SignatureHook.InterpretNotation(Image, 1, $"Successfully verified signature for {Image}\n", "").Verified);
    }

    [Fact]
    public void TheSuccessLineOnStderrOnly_IsNotVerified()
    {
        // Notation prints success on stdout. The same words on stderr are a message ABOUT something,
        // not the verdict.
        Assert.False(SignatureHook.InterpretNotation(Image, 0, "", $"Successfully verified signature for {Image}\n").Verified);
    }

    [Fact]
    public void TheSuccessLineEmbeddedInALongerLine_IsNotVerified()
    {
        Assert.False(SignatureHook.InterpretNotation(Image, 0, $"Warning: Successfully verified signature for {Image}\n", "").Verified);
    }

    // ---------------------------------------------------------------------------------------
    //  The verdict
    // ---------------------------------------------------------------------------------------

    private static ImageReference Admitted(string image) => new(image, image, null);

    [Fact]
    public void EveryImageVerified_Succeeds()
    {
        var images = new[] { Admitted(Image) };
        var results = new[] { new ImageVerification(Image, true, "ok") };

        Assert.Equal(HookStatus.SUCCEEDED, SignatureHook.Decide(images, results).Status);
    }

    [Fact]
    public void NoImagesAtAll_Fails()
    {
        // A task definition with nothing to verify is a gap the check cannot see into.
        Assert.Equal(HookStatus.FAILED, SignatureHook.Decide(Array.Empty<ImageReference>(), Array.Empty<ImageVerification>()).Status);
    }

    [Fact]
    public void ARefusedImage_Fails_EvenIfSomethingClaimsToHaveVerifiedIt()
    {
        var images = new[] { new ImageReference(Repo + ":latest", null, "a tag") };
        var results = new[] { new ImageVerification(Repo + ":latest", true, "ok") };

        Assert.Equal(HookStatus.FAILED, SignatureHook.Decide(images, results).Status);
    }

    [Fact]
    public void AnImageWithNoResult_Fails()
    {
        // Missing is not passing — the orchestrator skipping an image must not admit it.
        var second = Repo + "@sha256:" + new string('b', 64);
        var images = new[] { Admitted(Image), Admitted(second) };
        var results = new[] { new ImageVerification(Image, true, "ok") };

        var (status, reason) = SignatureHook.Decide(images, results);
        Assert.Equal(HookStatus.FAILED, status);
        Assert.Contains("never verified", reason);
    }

    [Fact]
    public void OneUnverifiedAmongVerified_Fails()
    {
        var second = Repo + "@sha256:" + new string('b', 64);
        var images = new[] { Admitted(Image), Admitted(second) };
        var results = new[] { new ImageVerification(Image, true, "ok"), new ImageVerification(second, false, "bad signature") };

        Assert.Equal((HookStatus.FAILED, "bad signature"), SignatureHook.Decide(images, results));
    }

    // ---------------------------------------------------------------------------------------
    //  The ECS contract
    // ---------------------------------------------------------------------------------------

    private static string Event(string stage = "PRE_SCALE_UP", string? revision = "arn:aws:ecs:us-west-2:503947800380:service-revision/scu-dev-cluster/scu-mp-aiphost/123") =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["executionId"] = "ecs-deployment-1",
            ["lifecycleStage"] = stage,
            ["resourceArn"] = "arn:aws:ecs:us-west-2:503947800380:service-deployment/scu-dev-cluster/scu-mp-aiphost/abc",
            ["executionDetails"] = new Dictionary<string, object?>
            {
                ["serviceArn"] = "arn:aws:ecs:us-west-2:503947800380:service/scu-dev-cluster/scu-mp-aiphost",
                ["targetServiceRevisionArn"] = revision,
                ["testTrafficWeights"] = new Dictionary<string, int>(),
                ["productionTrafficWeights"] = new Dictionary<string, int>(),
            },
            ["hookDetails"] = null,
        });

    [Fact]
    public void APreScaleUpEvent_Parses()
    {
        var e = SignatureHook.ParseEvent(Event());

        Assert.Equal("PRE_SCALE_UP", e.LifecycleStage);
        Assert.EndsWith("/123", e.TargetServiceRevisionArn);
        Assert.Equal("ecs-deployment-1", e.ExecutionId);
    }

    [Theory]
    [InlineData("POST_SCALE_UP")]
    [InlineData("PRODUCTION_TRAFFIC_SHIFT")]
    [InlineData("RECONCILE_SERVICE")]
    public void AnyOtherStage_IsRefused(string stage)
    {
        // Every later stage runs after new tasks exist; admission there is too late to be admission.
        Assert.Throws<InvalidOperationException>(() => SignatureHook.ParseEvent(Event(stage)));
    }

    [Fact]
    public void AnEventWithoutTheTargetRevision_IsRefused()
    {
        Assert.Throws<InvalidOperationException>(() => SignatureHook.ParseEvent(Event(revision: null)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    public void AnEventTheHookCannotRead_IsRefused(string json)
    {
        Assert.Throws<InvalidOperationException>(() => SignatureHook.ParseEvent(json));
    }

    [Theory]
    [InlineData(HookStatus.SUCCEEDED)]
    [InlineData(HookStatus.FAILED)]
    public void TheResponse_CarriesOnlyHookStatus(HookStatus status)
    {
        // Anything ECS does not accept makes the response invalid — which ECS treats as FAILED, turning
        // every admitted deploy into a rollback.
        using var doc = JsonDocument.Parse(SignatureHook.Response(status));
        var only = Assert.Single(doc.RootElement.EnumerateObject());

        Assert.Equal("hookStatus", only.Name);
        Assert.Equal(status.ToString(), only.Value.GetString());
    }

    [Fact]
    public void TheRegistryToken_SplitsAtTheFirstColonOnly()
    {
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes("AWS:pass:with:colons"));

        Assert.Equal(("AWS", "pass:with:colons"), SignatureHook.DecodeRegistryToken(token));
    }

    [Theory]
    [InlineData("%%%not-base64%%%")]
    [InlineData("bm9jb2xvbg==")] // "nocolon"
    public void ABadRegistryToken_IsRefused_WithoutEchoingIt(string token)
    {
        // A credential in an exception message ends up in a log.
        var ex = Assert.Throws<InvalidOperationException>(() => SignatureHook.DecodeRegistryToken(token));

        Assert.DoesNotContain(token, ex.Message);
        Assert.DoesNotContain("nocolon", ex.Message);
    }

    // ---------------------------------------------------------------------------------------
    //  Where Notation looks — every path from notation-go v1.3.2's source
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void TheLayout_MatchesNotationGosDirectoryRules()
    {
        var l = NotationLayout.Under("/tmp/lz-notation");

        // dir.UserConfigDir = os.UserConfigDir()/notation, and os.UserConfigDir is $XDG_CONFIG_HOME.
        Assert.Equal(l.ConfigHome + "/notation", l.ConfigDirectory);
        // dir.PathTrustPolicy = "trustpolicy.json".
        Assert.Equal(l.ConfigDirectory + "/trustpolicy.json", l.TrustPolicyPath);
        // UserLibexecDir defaults to UserConfigDir; a plugin is plugins/{name}/notation-{name}.
        Assert.Equal(
            l.ConfigDirectory + "/plugins/com.amazonaws.signer.notation.plugin/notation-com.amazonaws.signer.notation.plugin",
            l.PluginPath);
        // truststore/x509/{type}/{named store}.
        Assert.Equal(l.ConfigDirectory + "/truststore/x509/signingAuthority/aws-signer-ts", l.TrustStoreDirectory);
    }

    [Fact]
    public void TheTrustStoreTheLayoutCreates_IsTheOneThePolicyNames()
    {
        // Two spellings of one thing: if they ever disagree, every image fails with "trust store does
        // not exist", which reads like a broken install rather than a typo.
        var l = NotationLayout.Under("/tmp/x");
        using var doc = JsonDocument.Parse(SignatureHook.TrustPolicy(new[] { Profile }, Scopes));
        var named = doc.RootElement.GetProperty("trustPolicies")[0].GetProperty("trustStores")[0].GetString()!;
        var (type, name) = (named.Split(':')[0], named.Split(':')[1]);

        Assert.EndsWith($"/truststore/x509/{type}/{name}", l.TrustStoreDirectory);
    }

    [Fact]
    public void TheEnvironment_PointsNotationAtTheLayout_AndCarriesTheCredentials()
    {
        var l = NotationLayout.Under("/tmp/lz-notation");
        var env = l.Environment("AWS", "secret");

        Assert.Equal(l.ConfigHome, env["XDG_CONFIG_HOME"]);
        Assert.Equal(l.CacheHome, env["XDG_CACHE_HOME"]);
        // Lambda sets no HOME, and Go's os.UserConfigDir fails when neither variable is present.
        Assert.Equal(l.Home, env["HOME"]);
        // notation's SecureFlagOpts reads these.
        Assert.Equal("AWS", env["NOTATION_USERNAME"]);
        Assert.Equal("secret", env["NOTATION_PASSWORD"]);
    }

    [Theory]
    [InlineData("tmp/lz")]
    [InlineData("C:\\tmp")]
    [InlineData("")]
    public void TheLayout_IsAnAbsoluteLinuxPath(string root)
    {
        Assert.Throws<InvalidOperationException>(() => NotationLayout.Under(root));
    }

    [Fact]
    public void APackageWithoutTheVerifier_ReportsEveryMissingFile()
    {
        Assert.Equal(NotationLayout.PackagedFiles,
            NotationLayout.MissingFromPackage(new[] { "Lz.Aws.Deployer.dll" }));
        Assert.Empty(NotationLayout.MissingFromPackage(NotationLayout.PackagedFiles.Append("Lz.Aws.Deployer.dll")));
    }
}
