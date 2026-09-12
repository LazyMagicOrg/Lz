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
}
