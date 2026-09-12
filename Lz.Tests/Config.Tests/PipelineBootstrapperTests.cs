using Lz.Aws.Pipeline;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// The applier's one pure decision.
///
/// <para>It is a named function only because the obvious inline version was WRONG in the exact
/// condition a bootstrap runs in. The first real `--apply` against the greenfield build account on
/// 2026-09-12 threw <c>ArgumentNullException</c> from <c>.Any()</c>: the AWS SDK v4 returns NULL,
/// not an empty list, for a collection with no members, and an account with no OIDC providers is
/// precisely what a first bootstrap meets. Nothing had been created when it threw — the read is the
/// first step — so the failure was harmless, but only by luck of ordering.</para>
/// </summary>
public class PipelineBootstrapperTests
{
    private const string Arn = "arn:aws:iam::147440642635:oidc-provider/token.actions.githubusercontent.com";

    [Fact]
    public void ANullListMeansNotPresent_RatherThanThrowing()
    {
        // THE REGRESSION. This is what a greenfield account returns.
        Assert.False(PipelineBootstrapper.AlreadyHasProvider(null, Arn));
    }

    [Fact]
    public void AnEmptyListMeansNotPresent()
    {
        Assert.False(PipelineBootstrapper.AlreadyHasProvider(Array.Empty<string>(), Arn));
    }

    [Fact]
    public void AMatchingArnMeansPresent()
    {
        Assert.True(PipelineBootstrapper.AlreadyHasProvider(new[] { "arn:other", Arn }, Arn));
    }

    [Fact]
    public void AnotherAccountsProviderIsNotAMatch()
    {
        // The ARN carries the account id, so a provider in a different account must not be read as
        // this one already existing — that would skip creating it and leave every role trusting a
        // provider that is not there.
        var elsewhere = Arn.Replace("147440642635", "503947800380");

        Assert.False(PipelineBootstrapper.AlreadyHasProvider(new[] { elsewhere }, Arn));
    }

    [Fact]
    public void TheComparisonIsOrdinal_NotCaseInsensitive()
    {
        // ARNs are case-sensitive; treating them otherwise would be a guess rather than a check.
        Assert.False(PipelineBootstrapper.AlreadyHasProvider(new[] { Arn.ToUpperInvariant() }, Arn));
    }

    // ---------------------------------------------------------------------------------------
    //  The signing rule — where a guessed enum string cost an apply
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void TheFilterTypeIsTheSdkConstant_NotAGuessedString()
    {
        // THE REGRESSION. The literal "WILDCARD" was rejected by ECR with "Member must satisfy
        // enum value set: [WILDCARD_MATCH]", after every other resource had already been created.
        var rule = PipelineBootstrapper.BuildSigningRule("arn:profile", new[] { "scu-4df6-b9c6-aiphost" });

        Assert.Equal(Amazon.ECR.SigningRepositoryFilterType.WILDCARD_MATCH,
            rule.RepositoryFilters.Single().FilterType);
        Assert.Equal("WILDCARD_MATCH", rule.RepositoryFilters.Single().FilterType.Value);
    }

    [Fact]
    public void OneFilterPerRepository_AndTheProfileIsCarriedThrough()
    {
        var rule = PipelineBootstrapper.BuildSigningRule("arn:profile", new[] { "a", "b" });

        Assert.Equal("arn:profile", rule.SigningProfileArn);
        Assert.Equal(new[] { "a", "b" }, rule.RepositoryFilters.Select(f => f.Filter));
    }
}
