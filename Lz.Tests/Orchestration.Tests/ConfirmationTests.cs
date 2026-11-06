using Lz.Core.Interaction;

namespace Lz.Tests.Orchestration.Tests;

/// <summary>A confirmation with no terminal is a refusal with its own exit code, never a silent "no" that exits 0 (P3 D).</summary>
public class ConfirmationTests
{
    private static (bool Answer, string Out, string Err, int? Code, bool Read) Ask(bool redirected, string typed, bool exact = false, string? passInstead = "--yes")
    {
        var input = new TrackingReader(typed);
        var output = new StringWriter();
        var error = new StringWriter();
        int? code = null;
        var answer = Confirmation.Ask("Type 'yes' to confirm: ", redirected, input, output, error, c => code = c, passInstead, exact);
        return (answer, output.ToString(), error.ToString(), code, input.WasRead);
    }

    [Fact]
    public void WithNoTerminal_NothingIsAsked_AndTheCommandIsRefusedWithItsOwnCode()
    {
        var r = Ask(redirected: true, typed: "yes\n");
        Assert.False(r.Answer);
        Assert.False(r.Read);
        Assert.Equal("", r.Out);
        Assert.Equal(Confirmation.NoTerminalExitCode, r.Code);
        Assert.Contains("no terminal is attached", r.Err);
        Assert.Contains("Pass --yes", r.Err);
    }

    [Fact]
    public void WithNoTerminalAndNoFlag_ItDoesNotOfferOne()
        => Assert.DoesNotContain("Pass", Ask(redirected: true, typed: "", passInstead: null).Err);

    [Theory]
    [InlineData("yes\n", false, true)]
    [InlineData(" YES \n", false, true)]
    [InlineData(" YES \n", true, false)]
    [InlineData("no\n", false, false)]
    [InlineData("", false, false)]
    public void AtATerminal_OnlyYesConfirms_AndANoLeavesTheExitCodeAlone(string typed, bool exact, bool expected)
    {
        var r = Ask(redirected: false, typed: typed, exact: exact);
        Assert.Equal(expected, r.Answer);
        Assert.True(r.Read);
        Assert.Null(r.Code);
        Assert.Equal("Type 'yes' to confirm: ", r.Out);
    }

    private sealed class TrackingReader(string text) : StringReader(text)
    {
        public bool WasRead { get; private set; }
        public override string? ReadLine() { WasRead = true; return base.ReadLine(); }
    }
}
