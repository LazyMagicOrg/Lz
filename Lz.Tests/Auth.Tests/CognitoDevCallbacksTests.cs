using Lz.Aws.Auth;

namespace Lz.Tests.Auth.Tests;

/// <summary>
/// The localhost base paths behind a pool's SPA-client callback and logout URLs. No AWS: the component adds
/// <c>https://localhost:7218/{path}authentication/{login,logout}-callback</c> for each path, in this order.
/// </summary>
public class CognitoDevCallbacksTests
{
    private static readonly string[] BuiltIns = { "", "store/", "admin/", "app/" };

    [Fact]
    public void NoPoolPaths_GiveTheBuiltInsInTheirOrder()
    {
        // Six workspaces share this component: a pool that sets nothing must list the same URLs in the same order,
        // or every system's next preview shows a change to its Cognito clients.
        Assert.Equal(BuiltIns, CognitoDevCallbacks.BasePaths(null));
        Assert.Equal(BuiltIns, CognitoDevCallbacks.BasePaths(new List<string>()));
    }

    [Fact]
    public void PoolPaths_FollowTheBuiltIns()
    {
        Assert.Equal(
            BuiltIns.Append("seller/"),
            CognitoDevCallbacks.BasePaths(new List<string> { "seller/" }));
    }

    [Fact]
    public void APathAlreadyListed_IsRegisteredOnce()
    {
        // Without this, a repeated path would put the same URL in the client's list twice.
        Assert.Equal(
            BuiltIns.Append("seller/"),
            CognitoDevCallbacks.BasePaths(new List<string> { "admin/", "seller/", "seller/" }));
    }
}
