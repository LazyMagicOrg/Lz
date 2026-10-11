using Lz.Aws.Shared;
using Xunit;

namespace Lz.Tests.Auth.Tests;

/// <summary>
/// THE CONSUMER SESSION LASTS AS LONG AS ITS REFRESH TOKEN. The host's session length for the consumerauth BFF
/// instance (<c>LZ_CBFF_SESSION_TTL_HOURS</c>) is the hours the foundation exported for that pool's BFF client,
/// which is the pool's <c>BffSessionTtlHours</c> and the client's <c>RefreshTokenValidity</c>. It was a constant
/// in the tenant wiring beside a pool setting with a different default. These pin how the exported value is read:
/// a stack reference hands back an object, and a session of no length must never come of a value it cannot read.
/// </summary>
public class BffSessionTtlTests
{
    private const string Before = BffWiring.ConsumerSessionTtlHoursWhenNotExported;

    [Fact]
    public void BeforeTheFoundationExportsIt_TheConsumerSessionIsWhatItWas()
        => Assert.Equal("720", Before);

    [Theory]
    [InlineData("2160", "2160")]      // ninety days, as the foundation writes it
    [InlineData(" 12 ", "12")]
    [InlineData("1", "1")]
    public void AnExportedNumberOfHours_IsTheSessionLength(string exported, string expected)
        => Assert.Equal(expected, BffWiring.SessionTtlHoursOf(exported, Before));

    [Fact]
    public void ANumberThatCameBackAsANumber_IsReadTheSame()
    {
        Assert.Equal("2160", BffWiring.SessionTtlHoursOf(2160, Before));
        Assert.Equal("2160", BffWiring.SessionTtlHoursOf(2160L, Before));
        Assert.Equal("2160", BffWiring.SessionTtlHoursOf(2160d, Before));   // JSON has one number type
    }

    [Theory]
    [InlineData(null)]                // a foundation deployed before the export
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0")]                 // never a session of no length
    [InlineData("-5")]
    [InlineData("12.5")]
    [InlineData("ninety days")]
    public void WhatIsNotAWholeNumberOfHoursAboveZero_IsTheFallback(string? exported)
        => Assert.Equal(Before, BffWiring.SessionTtlHoursOf(exported, Before));

    [Fact]
    public void AValueOfAnotherKind_IsTheFallback()
    {
        Assert.Equal(Before, BffWiring.SessionTtlHoursOf(new object(), Before));
        Assert.Equal(Before, BffWiring.SessionTtlHoursOf(0d, Before));
        Assert.Equal(Before, BffWiring.SessionTtlHoursOf(12.5d, Before));
        Assert.Equal("8", BffWiring.SessionTtlHoursOf(null, "8"));           // the fallback is the caller's
    }
}
