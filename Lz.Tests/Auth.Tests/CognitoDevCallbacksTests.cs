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

    // ---- ports -----------------------------------------------------------------------------------------------------

    [Fact]
    public void NoPoolPorts_GiveTheSameUrlsAsBefore_InTheirOrder()
    {
        // The component registered https://localhost:7218/{path} for each path before ports could be named; a pool
        // that names none must list exactly those, or every system's next preview shows a change to its clients.
        var before = BuiltIns.Append("seller/").Select(path => $"https://localhost:7218/{path}");

        Assert.Equal(before, CognitoDevCallbacks.BaseUrls(null, new List<string> { "seller/" }));
        Assert.Equal(before, CognitoDevCallbacks.BaseUrls(new List<int>(), new List<string> { "seller/" }));
    }

    [Fact]
    public void APoolsPorts_ReplaceTheDefault()
    {
        var urls = CognitoDevCallbacks.BaseUrls(new List<int> { 7219 }, null);

        Assert.Equal(BuiltIns.Select(path => $"https://localhost:7219/{path}"), urls);
        Assert.DoesNotContain(urls, url => url.Contains(":7218/"));
    }

    [Fact]
    public void SeveralPorts_EachGetEveryPath_PortByPort()
    {
        Assert.Equal(
            new[]
            {
                "https://localhost:7218/", "https://localhost:7218/store/", "https://localhost:7218/admin/",
                "https://localhost:7218/app/", "https://localhost:7218/seller/",
                "https://localhost:7219/", "https://localhost:7219/store/", "https://localhost:7219/admin/",
                "https://localhost:7219/app/", "https://localhost:7219/seller/",
            },
            CognitoDevCallbacks.BaseUrls(new List<int> { 7218, 7219 }, new List<string> { "seller/" }));
    }

    [Fact]
    public void APortListedTwice_IsRegisteredOnce()
        => Assert.Equal(new[] { 7219, 7218 }, CognitoDevCallbacks.Ports(new List<int> { 7219, 7218, 7219 }));
}
