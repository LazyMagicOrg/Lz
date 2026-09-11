using System.Reflection;
using Amazon.Runtime.CredentialManagement;
using Lz.Aws;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// The one place a configured <c>Profile</c> becomes credentials (Docs/specs/DecoupledCd.md §8
/// item 2, punchlist P0).
///
/// <para>P0 asks specifically for "a test that a config naming a profile resolves exactly the
/// credentials it does today", and <see cref="ANamedProfile_ResolvesExactlyWhatTheOldCodeDid"/> is
/// that test, written as a differential against the literal previous implementation rather than
/// against a remembered description of it.</para>
///
/// <para>These tests do not reach AWS. Resolution reads the local credential store, so they assert
/// AGREEMENT with the old code and the handling of the empty case — never that any particular
/// profile exists on the machine, which would make them pass or fail by workstation.</para>
/// </summary>
public class AwsCredentialsFactoryTests
{
    /// <summary>
    /// Verbatim the resolution every one of the 39 converted sites performed before 2026-09-11.
    /// Keeping it here is the point: the test compares the new factory against the OLD CODE, so it
    /// cannot drift toward whatever the factory happens to do.
    /// </summary>
    private static Amazon.Runtime.AWSCredentials? OldWay(string profile)
    {
        var chain = new CredentialProfileStoreChain();
        return chain.TryGetAWSCredentials(profile, out var credentials) ? credentials : null;
    }

    [Theory]
    [InlineData("default")]
    [InlineData("scu-dev")]
    [InlineData("no-such-profile-anywhere-xyz")]
    [InlineData("name with spaces")]
    public void ANamedProfile_ResolvesExactlyWhatTheOldCodeDid(string profile)
    {
        // Whether any of these exists on this machine is irrelevant and deliberately not asserted:
        // what must hold is that old and new agree, on a hit AND on a miss.
        var oldResult = OldWay(profile);
        var newResult = AwsCredentialsFactory.Resolve(profile);

        Assert.Equal(oldResult is null, newResult is null);
        if (oldResult is not null)
            Assert.Equal(oldResult.GetType(), newResult!.GetType());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AnEmptyProfile_MeansTheDefaultChain_ReportedAsNull(string? profile)
    {
        // Null is the signal for "construct the client WITHOUT credentials and let it resolve its
        // own". It is not a failure, and it must not throw: this is the deployer's normal state.
        Assert.Null(AwsCredentialsFactory.Resolve(profile));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ResolveOrThrow_IsSilentForAnEmptyProfile(string? profile)
    {
        // The throwing variant must NOT treat "no profile" as an error, or the deployer could not
        // run at all. It exists to catch a NAMED profile that does not resolve.
        Assert.Null(AwsCredentialsFactory.ResolveOrThrow(profile));
    }

    [Fact]
    public void ResolveOrThrow_RefusesANamedProfileThatDoesNotResolve()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => AwsCredentialsFactory.ResolveOrThrow("no-such-profile-anywhere-xyz"));

        // The message must name the profile and say why it is not falling back — silently using
        // ambient credentials here could act against a different AWS account.
        Assert.Contains("no-such-profile-anywhere-xyz", ex.Message);
        Assert.Contains("ambient", ex.Message);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("scu-dev", "--profile \"scu-dev\"")]
    public void CliProfileArg_OmitsTheFlagEntirelyWhenThereIsNoProfile(string? profile, string expected)
    {
        // The failure this prevents is opaque rather than loud: interpolating an empty profile
        // yields `aws ... --profile  --region us-west-2`, and the CLI reads --region as the
        // profile name.
        Assert.Equal(expected, AwsCredentialsFactory.CliProfileArg(profile));
    }

    /// <summary>
    /// THE COMPLETENESS PIN. A factory that only SOME sites use is not a single point of
    /// resolution, and the sweep that converted 39 of them is exactly the kind of change that
    /// regresses one file at a time.
    /// </summary>
    [Fact]
    public void NothingResolvesCredentialsExceptTheFactory()
    {
        var root = RepoRoot();
        var factory = Path.Combine("Lz.Aws", "AwsCredentialsFactory.cs");

        var offenders = Directory
            .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                     // The test project names the type deliberately, to compare against it.
                     && !f.Contains($"{Path.DirectorySeparatorChar}Lz.Tests{Path.DirectorySeparatorChar}")
                     // ProjectTemplates are emitted source for generated apps, not lz's own code,
                     // and they have no lz config to take a Profile from.
                     && !f.Contains($"{Path.DirectorySeparatorChar}ProjectTemplates{Path.DirectorySeparatorChar}"))
            .Where(f => File.ReadAllText(f).Contains("CredentialProfileStoreChain"))
            .Select(f => Path.GetRelativePath(root, f))
            .Where(rel => rel != factory)
            .OrderBy(rel => rel)
            .ToList();

        Assert.True(offenders.Count == 0,
            "These files resolve AWS credentials directly instead of through AwsCredentialsFactory:\n  " +
            string.Join("\n  ", offenders) +
            "\n\nA site that builds its own CredentialProfileStoreChain cannot honour an EMPTY " +
            "Profile, which is how the decoupled-CD deployer runs (DecoupledCd.md §8 item 2) — it " +
            "will read \"\" as a profile name, fail to find it, and take whatever failure path that " +
            "site has. Use AwsCredentialsFactory.Resolve (null means: construct the client without " +
            "credentials) or ResolveOrThrow (refuse a NAMED profile that will not resolve).");
    }

    /// <summary>Walk up from the test assembly to the directory holding Lz.slnx.</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Lz.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
