using Lz.Tests.Build.Tests;

namespace Lz.Tests.Compute.Tests;

/// <summary>
/// What a tenant's service may do to the logins in its pools. The grant is an inline policy written in each topology's
/// service component, so a topology can lose an action the other has and nothing would say so until a deployed
/// service was refused. This reads the statement each component writes.
///
/// <para><b>AdminSetUserPassword</b> (owner, 2026-11-13: "Grant the permission in Lz"): a service that mails its own
/// login invitations sends one AGAIN by setting a new temporary password and mailing that. Without the action the
/// service is refused and falls back to the pool's own sender - the sender the service's invitations were moved off
/// because of its small daily limit. It is granted on the pools the role already administers, beside AdminCreateUser,
/// which sets a temporary password too; it reaches no pool that statement does not.</para>
/// </summary>
public class ServiceRoleCognitoGrantTests
{
    private static string LzAws() => Path.Combine(PackageHandlingScratchBuild.FindLzRepoRoot(), "Lz.Aws");

    /// <summary>The actions of the one statement in <paramref name="relative"/> that names a Cognito action.</summary>
    private static List<string> CognitoActions(string relative)
    {
        var path = Path.Combine(LzAws(), relative.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"{relative} has moved; this test names it");
        var lines = File.ReadAllLines(path).Where(l => l.Contains("cognito-idp:Admin", StringComparison.Ordinal)).ToList();
        var line = Assert.Single(lines);
        Assert.Contains("\"\"Action\"\":", line);
        return System.Text.RegularExpressions.Regex.Matches(line, @"cognito-idp:(\w+)").Select(m => m.Groups[1].Value).ToList();
    }

    [Theory]
    [InlineData("Compute/Fargate/AwsFargateTenantServiceComponent.cs")]
    [InlineData("Compute/Lambda/AwsLambdaTenantServiceComponent.cs")]
    public void ATenantsService_MayCreateALogin_AndSetItsTemporaryPasswordAgain(string relative)
    {
        var actions = CognitoActions(relative);
        Assert.Contains("AdminCreateUser", actions);
        Assert.Contains("AdminGetUser", actions);          // the resend reads the login first: only one nobody has used
        Assert.Contains("AdminSetUserPassword", actions);
        Assert.Equal(actions.Count, actions.Distinct().Count());
    }

    /// <summary>The whole list, so an action added to one topology's service is decided for the other.</summary>
    [Fact]
    public void TheFargateService_HasWhatTheLambdaServiceHas_AndTheSessionActsBesides()
    {
        var fargate = CognitoActions("Compute/Fargate/AwsFargateTenantServiceComponent.cs");
        var lambda = CognitoActions("Compute/Lambda/AwsLambdaTenantServiceComponent.cs");
        Assert.Equal(
            new[] { "AdminCreateUser", "AdminDeleteUser", "AdminGetUser", "AdminSetUserPassword", "AdminUpdateUserAttributes", "ListUsers" },
            lambda.OrderBy(a => a, StringComparer.Ordinal));
        Assert.Equal(
            lambda.Concat(new[] { "AdminDisableUser", "AdminEnableUser", "AdminUserGlobalSignOut" }).OrderBy(a => a, StringComparer.Ordinal),
            fargate.OrderBy(a => a, StringComparer.Ordinal));
    }
}
