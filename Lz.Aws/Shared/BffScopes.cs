using Lz.Aws.Config;

namespace Lz.Aws.Shared;

/// <summary>
/// THE SCOPES A BFF CLIENT ASKS FOR (ScutaraDocs specs/Buyers/BuyersApp/BuyerAppReactPunchlist.md BR-D1). Always
/// <c>openid profile email</c>; and, where the pool's <c>McpResource.GrantToBffClient</c> asks for it, the pool's own
/// qualified MCP scope, so the access token a cookie session carries is one the host's <c>/mcp</c> takes - and a web
/// client signed in through the BFF can call the MCP tools with no token in the browser. One list, used twice: what
/// Cognito allows the client (AwsCognitoComponent) and what the host asks for at sign-in (BffWiring, through the
/// foundation's <c>auth_{pool}_bffScopes</c> export), so the two cannot disagree.
/// </summary>
public static class BffScopes
{
    public const string Identity = "openid profile email";

    /// <summary>
    /// PURE. The MCP scope to grant <paramref name="pool"/>'s BFF client, or null for none. Refuses a grant that
    /// cannot be made - no BFF client, or no MCP resource - rather than leaving it silently off.
    /// </summary>
    public static string? McpScopeForBff(string pool, AwsAuthConfigEntry config, string? qualifiedMcpScope)
    {
        if (config.McpResource is not { GrantToBffClient: true })
            return null;
        if (!config.ProvisionBffClient)
            throw new InvalidOperationException(
                $"Pool '{pool}' McpResource.GrantToBffClient is set but ProvisionBffClient is not: there is no BFF client to grant the scope to.");
        if (string.IsNullOrWhiteSpace(qualifiedMcpScope))
            throw new InvalidOperationException($"Pool '{pool}' McpResource.GrantToBffClient is set but the pool has no MCP scope.");
        return qualifiedMcpScope;
    }

    /// <summary>PURE. The space-separated scopes for a BFF client granted <paramref name="mcpScope"/> (or none).</summary>
    public static string ScopesOf(string? mcpScope) =>
        string.IsNullOrWhiteSpace(mcpScope) ? Identity : Identity + " " + mcpScope.Trim();

    /// <summary>
    /// PURE. The scopes as the host's env takes them, from what a stack reference returned for the foundation's
    /// export: a string that starts with the identity scopes. Anything else - no export (a foundation deployed before
    /// it exported one), an empty one, another type - is the identity scopes alone, as every BFF asked for before.
    /// </summary>
    public static string Of(object? exported) =>
        exported is string s && s.Trim().StartsWith(Identity, StringComparison.Ordinal) ? s.Trim() : Identity;
}
