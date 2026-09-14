using Lz.Aws.Pipeline;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// The bootstrapper's wait for a role it has just created (DecoupledCd.md P4 stage C): which of Lambda's refusals are
/// IAM's eventual consistency, retried, and which are a wrong role, failed.
/// </summary>
public class DeployerBootstrapperRetryTests
{
    [Theory]
    // The refusal the retry was written for.
    [InlineData("The role defined for the function cannot be assumed by Lambda.")]
    // VERBATIM from creating scu-dev-deployer-verify-bundle seconds after its role, 2026-09-14.
    // The apply stopped here, half-way through dev's functions.
    [InlineData("Lambda was unable to configure access to your environment variables because the KMS key is invalid for CreateGrant. " +
                "Please check your KMS key settings. KMS Exception: InvalidArnException KMS Message: ARN does not refer to a valid " +
                "principal: arn:aws:sts::503947800380:assumed-role/scu-dev-deployer-verify-bundle-fn/scu-dev-deployer-verify-bundle")]
    public void ARoleIamHasNotFinishedCreating_IsWaitedFor(string message)
        => Assert.True(DeployerBootstrapper.RoleNotYetUsableByLambda(message));

    [Theory]
    // A KMS refusal that waiting does not change: the role may not use the key.
    [InlineData("Lambda was unable to configure access to your environment variables because the KMS key is invalid for CreateGrant. " +
                "Please check your KMS key settings. KMS Exception: AccessDeniedException KMS Message: User is not authorized to perform kms:CreateGrant")]
    [InlineData("The runtime parameter of dotnet6 is no longer supported for creating or updating AWS Lambda functions.")]
    [InlineData("Value 'x' at 'role' failed to satisfy constraint: Member must satisfy regular expression pattern")]
    public void AnythingElse_FailsAtOnce(string message)
        => Assert.False(DeployerBootstrapper.RoleNotYetUsableByLambda(message));

    [Fact]
    public void CreateFunction_RetriesOnThatPredicate_AndNothingElse()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Lz.slnx"))) dir = dir.Parent;
        var src = File.ReadAllText(Path.Combine(dir!.FullName, "Lz.Aws", "Pipeline", "DeployerBootstrapper.cs"));

        Assert.Contains("when (attempt < 20 && RoleNotYetUsableByLambda(ex.Message))", src);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(src, @"catch \(InvalidParameterValueException"));
    }
}
