using Lz.Tests.Build.Tests;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// Pins what the review's fix batch (DecoupledCd.md §14.2, §14.3) changed in the two pipeline bootstraps, where the plans'
/// tests cannot see it: the order signing and roles are applied in, what is read back after it is written, which credential
/// resolver runs, and how a bucket is asked for. Lz.Tests has no AWS harness, so — like <see cref="AlertsCallSiteTests"/> —
/// this reads the source.
/// </summary>
public class BootstrapHardeningCallSiteTests
{
    private static string Source(params string[] path)
    {
        var file = Path.Combine(new[] { PackageHandlingScratchBuild.FindLzRepoRoot() }.Concat(path).ToArray());
        Assert.True(File.Exists(file), $"source not found at {file}");
        return File.ReadAllText(file);
    }

    private static int At(string src, string call, string where)
    {
        var i = src.IndexOf(call, StringComparison.Ordinal);
        Assert.True(i >= 0, $"{where} no longer contains {call}");
        return i;
    }

    private static int Count(string src, string text)
    {
        var n = 0;
        for (var i = src.IndexOf(text, StringComparison.Ordinal); i >= 0; i = src.IndexOf(text, i + text.Length, StringComparison.Ordinal))
            n++;
        return n;
    }

    /// <summary>The body of the method that starts at <paramref name="signature"/>, up to the next method's doc comment.</summary>
    private static string Body(string src, string signature)
    {
        var start = At(src, signature, "the source");
        var end = src.IndexOf("\n    /// <summary>", start, StringComparison.Ordinal);
        return end < 0 ? src[start..] : src[start..end];
    }

    // ---------------------------------------------------------------------------------------
    //  The build account
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void TheBuildBootstrap_WritesTheSigningRules_BeforeAnyRoleGitHubAssumes()
    {
        // The first apply created a role and its repository and then failed at the signing call: a role that could push
        // images nothing would sign. Profiles and repositories, then the rules, then the roles.
        const string file = "PipelineBootstrapper.cs";
        var src = Source("Lz.Aws", "Pipeline", file);

        var profile = At(src, "profileArn = await EnsureSigningProfileAsync(signer, sp);", file);
        var repository = At(src, "await EcrRepositoryHardening.EnsureAsync(ecr, repo,", file);
        var signing = At(src, "await ApplySigningConfigurationAsync(ecr, signingRules);", file);
        var roles = At(src, "await EnsureRoleAsync(iam, role, providerArn);", file);

        Assert.True(profile < signing && repository < signing, "the signing rules are written before the profiles or repositories they name");
        Assert.True(signing < roles, "a role GitHub assumes exists before the rules that sign its pushes");
        Assert.Equal(1, Count(src, "await EnsureRoleAsync("));
    }

    [Fact]
    public void TheSigningConfiguration_IsMergedWithTheRegistrys_AndReadBack()
    {
        // One configuration per registry, and every environment's run writes it: replaced, prod's run would drop dev's rule.
        const string file = "PipelineBootstrapper.cs";
        var body = Body(Source("Lz.Aws", "Pipeline", file), "private static async Task ApplySigningConfigurationAsync(");

        var read = At(body, "var existing = await SigningRulesAsync(ecr);", file);
        var merge = At(body, "var merged = CrossAccount.MergeSigning(existing, rules);", file);
        var put = At(body, "SigningConfiguration = new Amazon.ECR.Model.SigningConfiguration { Rules = merged },", file);
        var readBack = At(body, "var missing = CrossAccount.SigningRulesNotHeld(await SigningRulesAsync(ecr), rules);", file);

        Assert.True(read < merge && merge < put && put < readBack);
        Assert.DoesNotContain("Rules = rules", body);
    }

    // ---------------------------------------------------------------------------------------
    //  Both bootstraps
    // ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData("PipelineBootstrapper.cs", 2)]
    // Five since P4 stage C: the client targets' distributions are read with the same credentials.
    [InlineData("DeployerBootstrapper.cs", 5)]
    public void ANamedProfileThatDoesNotResolve_IsRefused_NotReplacedByAmbientCredentials(string file, int resolutions)
    {
        // Falling back labelled the ambient account with the profile's name in the dry run, and applied there.
        var src = Source("Lz.Aws", "Pipeline", file);

        Assert.DoesNotContain("AwsCredentialsFactory.Resolve(", src);
        Assert.Equal(resolutions, Count(src, "AwsCredentialsFactory.ResolveOrThrow(profile)"));
    }

    [Theory]
    [InlineData("PipelineBootstrapper.cs", "await s3.PutBucketAsync(S3BucketRequests.Create(store.Name, region));")]
    [InlineData("DeployerBootstrapper.cs", "await s3.PutBucketAsync(S3BucketRequests.Create(bucket, region));")]
    public void APipelineBucket_IsAskedForThroughTheOneHelper(string file, string call)
    {
        var src = Source("Lz.Aws", "Pipeline", file);

        At(src, call, file);
        Assert.Equal(1, Count(src, "PutBucketAsync("));
        Assert.DoesNotContain("new PutBucketRequest", src);
    }

    [Fact]
    public void AWebAppBucket_TheBundleDeployCreates_IsAskedForThroughTheSameHelper()
    {
        // P-7: the deployer's DeployBundle function creates a client app's bucket, in whatever region the system runs.
        var src = Source("Lz.Aws.Deployer", "Adapters.cs");

        At(src, "await s3.PutBucketAsync(Lz.Aws.S3BucketRequests.Create(bucket, region));", "Adapters.cs");
        Assert.Equal(1, Count(src, "PutBucketAsync("));
        Assert.DoesNotContain("new PutBucketRequest", src);
    }

    [Fact]
    public void TheStateBucket_IsAskedForThroughTheSameHelper()
    {
        var src = Source("Lz.Aws", "AwsStateBootstrapper.cs");

        At(src, "await client.PutBucketAsync(S3BucketRequests.Create(bucketName, region));", "AwsStateBootstrapper.cs");
        Assert.DoesNotContain("new PutBucketRequest", src);
    }

    // ---------------------------------------------------------------------------------------
    //  The target account
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void EachDeployerRolesTrust_IsThePlans_AndIsReadBack()
    {
        const string file = "DeployerBootstrapper.cs";
        var src = Source("Lz.Aws", "Pipeline", file);

        At(src, "await EnsureRoleAsync(iam, plan.RoleName, plan.StateMachineTrustPolicy,", file);
        At(src, "await EnsureRoleAsync(iam, fn.RoleName, plan.FunctionTrustPolicy,", file);
        At(src, "await EnsureRoleAsync(iam, plan.HookInvokerRoleName, plan.HookInvokerTrustPolicy,", file);
        Assert.Equal(3, Count(src, "await EnsureRoleAsync("));

        var body = Body(src, "private static async Task<string> EnsureRoleAsync(");
        // No trust written here: it is planned, where a test reads its conditions.
        Assert.DoesNotContain("Principal", body);

        var update = At(body, "RoleName = roleName, PolicyDocument = trust,", file);
        var create = At(body, "AssumeRolePolicyDocument = trust,", file);
        var readBack = At(body, "await RequireTrustAsync(iam, roleName, trust);", file);
        Assert.True(update < readBack && create < readBack, "the trust is read back before it is written");

        var require = Body(src, "private static async Task RequireTrustAsync(");
        var compare = At(require, "stored = document is null ? null : Uri.UnescapeDataString(document);", file);
        At(require[compare..], "if (CrossAccount.SamePolicy(trust, stored))", file);
        var refuse = At(require, "throw new InvalidOperationException(", file);
        Assert.True(compare < refuse);
    }

    [Fact]
    public void EachFunctionsLogGroup_GetsItsRetention_BeforeTheFunctionIsWritten()
    {
        const string file = "DeployerBootstrapper.cs";
        var src = Source("Lz.Aws", "Pipeline", file);

        var loop = At(src, "foreach (var fn in plan.Functions)", file);
        var group = At(src, "await EnsureLogGroupAsync(logs, $\"/aws/lambda/{fn.Name}\", retention);", file);
        var function = At(src, "await EnsureFunctionAsync(lambda, fn, fnRole, packages[fn.Package]);", file);
        Assert.True(loop < group && group < function, "a function could log into a group that never expires");
        At(src, "if (plan.LogRetentionDays is int retention)", file);

        var body = Body(src, "private static async Task EnsureLogGroupAsync(");
        var put = At(body, "await logs.PutRetentionPolicyAsync(", file);
        var readBack = At(body, "if (group?.RetentionInDays != retentionDays)", file);
        Assert.True(put < readBack);
    }
}
