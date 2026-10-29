using Lz.Aws.Config;
using Lz.Aws.Shared;
using Xunit;

namespace Lz.Tests.Auth.Tests;

/// <summary>
/// A BFF CLIENT MAY BE GRANTED ITS POOL'S MCP SCOPE (McpResource.GrantToBffClient), so a web client signed in through
/// the BFF can call /mcp with its cookie. These pin the one list both halves use - what Cognito allows the client and
/// what the host asks for - that it is unchanged where nobody asks, and that a grant which cannot be made is refused.
/// </summary>
public class BffScopesTests
{
    private const string Mcp = "https://match.aiproxydev.click/mcp/invoke";

    private static AwsAuthConfigEntry Pool(bool bff, bool grant, bool mcp = true) => new()
    {
        ProvisionBffClient = bff,
        McpResource = mcp ? new McpResourceConfig { Identifier = "https://match.aiproxydev.click/mcp", GrantToBffClient = grant } : null,
    };

    [Fact]
    public void Unasked_TheBffClientKeepsTheIdentityScopes()
    {
        Assert.Null(BffScopes.McpScopeForBff("consumerauth", Pool(bff: true, grant: false), Mcp));
        Assert.Null(BffScopes.McpScopeForBff("businessauth", Pool(bff: true, grant: false, mcp: false), null));
        Assert.Equal("openid profile email", BffScopes.ScopesOf(null));
    }

    [Fact]
    public void Asked_TheBffClientIsGrantedThePoolsMcpScope()
    {
        var scope = BffScopes.McpScopeForBff("consumerauth", Pool(bff: true, grant: true), Mcp);
        Assert.Equal(Mcp, scope);
        Assert.Equal("openid profile email " + Mcp, BffScopes.ScopesOf(scope));
    }

    [Fact]
    public void AGrantWithNoBffClient_IsRefused()
        => Assert.Throws<InvalidOperationException>(() => BffScopes.McpScopeForBff("consumerauth", Pool(bff: false, grant: true), Mcp));

    [Fact]
    public void AGrantWithNoMcpScope_IsRefused()
        => Assert.Throws<InvalidOperationException>(() => BffScopes.McpScopeForBff("consumerauth", Pool(bff: true, grant: true), null));

    [Theory]
    [InlineData("openid profile email " + Mcp, "openid profile email " + Mcp)]
    [InlineData(" openid profile email ", "openid profile email")]
    public void TheExportIsReadAsWritten(string exported, string expected)
        => Assert.Equal(expected, BffScopes.Of(exported));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(Mcp)]   // a list without the identity scopes is not one the BFF can sign in with
    public void BeforeTheFoundationExportsIt_TheHostAsksForTheIdentityScopes(string? exported)
        => Assert.Equal("openid profile email", BffScopes.Of(exported));

    [Fact]
    public void AnExportOfAnotherType_IsTheIdentityScopes()
        => Assert.Equal("openid profile email", BffScopes.Of(2160d));
}
