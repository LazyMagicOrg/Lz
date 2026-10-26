using System.Text;
using Lz.Aws.Pipeline;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// GATE 1 FOR A SITE (DecoupledCd.md V-24): lz's own check of the bundles one run built, one per environment, which runs no
/// repository code. Each bundle: the pages park mode and the edge rely on exist; every local link and asset resolves inside
/// it; its canonical and Open Graph URLs name one host; <c>noindex</c> is where the environment says; every same-origin
/// stylesheet and script carries SRI, and a third-party one comes from a named host. Across bundles: the same files, the
/// same bytes once each bundle's host and its <c>noindex</c> are set aside — so what dev served is what prod serves.
/// </summary>
public class SiteCheckTests
{
    private const string Css = "/css/main.min.abc.css";

    private static string Page(string host, string path, bool noindex, string body = "", string? extraHead = null)
        => $"<!doctype html><html lang=en><head><meta charset=utf-8>" +
           (noindex ? "<meta name=robots content=\"noindex, nofollow\">" : "") +
           $"<title>Scutara</title><link rel=canonical href=https://{host}{path}>" +
           $"<meta property=og:url content=\"https://{host}{path}\">" +
           $"<link rel=stylesheet href={Css} integrity=\"sha256-abc\" crossorigin=anonymous>" +
           (extraHead ?? "") + "</head><body>" + body + "</body></html>";

    private static Dictionary<string, byte[]> Files(string host, bool noindex, Action<Dictionary<string, string>>? change = null)
    {
        var text = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["index.html"] = Page(host, "/", noindex, $"<a href=/about/>About</a><a href=https://{host}/about/>Again</a>" +
                "<img src=/images/hero.webp srcset=\"/images/hero.webp 1x, /images/hero@2x.webp 2x\" alt=\"\">" +
                "<a href=https://cal.example.com/book>Book</a><a href=mailto:hi@example.com>Mail</a><a href=#faq>FAQ</a>"),
            ["about/index.html"] = Page(host, "/about/", noindex, "<a href=../>Home</a><a href=/about>Self</a>"),
            ["404.html"] = Page(host, "/404.html", noindex),
            ["park.html"] = "<!doctype html><html><head>" + (noindex ? "<meta name=robots content=noindex>" : "") + "<title>Parked</title></head><body>Back soon</body></html>",
            [Css[1..]] = "body{margin:0}",
        };
        change?.Invoke(text);
        var files = text.ToDictionary(kv => kv.Key, kv => Encoding.UTF8.GetBytes(kv.Value), StringComparer.Ordinal);
        files["images/hero.webp"] = new byte[] { 1, 2, 3 };
        files["images/hero@2x.webp"] = new byte[] { 4, 5, 6 };
        return files;
    }

    private static SiteBundle Dev(Action<Dictionary<string, string>>? change = null) => new("dev", Files("aiproxydev.click", noindex: true, change));
    private static SiteBundle Test(Action<Dictionary<string, string>>? change = null) => new("test", Files("scutara-test.click", noindex: true, change));
    private static SiteBundle Prod(Action<Dictionary<string, string>>? change = null) => new("prod", Files("scutara.com", noindex: false, change));

    private static readonly SiteCheckSettings Settings = new(Indexed: new[] { "prod" }, ThirdPartyHosts: new[] { "link.msgsndr.com" });

    private static SiteCheckResult Check(params SiteBundle[] bundles) => SiteCheck.Check(bundles, Settings);

    private static void Refused(SiteCheckResult result, string reason)
    {
        Assert.False(result.Passed);
        Assert.Contains(result.Reasons, r => r.Contains(reason, StringComparison.Ordinal));
    }

    [Fact]
    public void SoundBundlesForEveryEnvironment_Pass()
    {
        var result = Check(Dev(), Test(), Prod());
        Assert.True(result.Passed, string.Join("\n", result.Reasons));
        Assert.Equal("aiproxydev.click", result.Hosts["dev"]);
        Assert.Equal("scutara.com", result.Hosts["prod"]);
    }

    [Fact]
    public void OneBundle_IsCheckedOnItsOwn()
        => Assert.True(Check(Dev()).Passed);

    [Fact]
    public void NoBundle_IsRefused()
        => Refused(Check(), "no site bundle to check");

    [Theory]
    [InlineData("index.html")]
    [InlineData("404.html")]
    [InlineData("park.html")]
    public void APageTheEdgeRelieson_MustExist(string page)
        => Refused(Check(Dev(t => t.Remove(page))), $"dev: {page} is missing");

    [Fact]
    public void ABrokenLocalLink_IsNamedWithItsPage()
        => Refused(Check(Dev(t => t["about/index.html"] = Page("aiproxydev.click", "/about/", true, "<a href=/pricing/>Pricing</a>"))),
            "dev: about/index.html links to /pricing/, which is not in the bundle");

    [Fact]
    public void AMissingImageInASrcset_IsNamed()
        => Refused(Check(Dev(t => t["index.html"] = Page("aiproxydev.click", "/", true, "<img src=/images/hero.webp srcset=\"/images/hero.webp 1x, /images/gone@2x.webp 2x\">"))),
            "links to /images/gone@2x.webp");

    [Fact]
    public void AnAbsoluteLinkToItsOwnHost_IsLocal()
        => Refused(Check(Dev(t => t["index.html"] = Page("aiproxydev.click", "/", true, "<a href=https://aiproxydev.click/missing/>x</a>"))),
            "links to https://aiproxydev.click/missing/");

    [Fact]
    public void ALinkInsideAScript_IsNotALink()
        => Assert.True(Check(Dev(t => t["index.html"] = Page("aiproxydev.click", "/", true, "<script>var a='<a href=/nowhere/>';</script>"))).Passed);

    [Fact]
    public void AQueryOrFragment_IsNotPartOfThePath()
        => Assert.True(Check(Dev(t => t["index.html"] = Page("aiproxydev.click", "/", true, "<a href=\"/about/?utm=1#team\">x</a>"))).Passed);

    [Fact]
    public void TheIndexMustNameItsCanonicalHost()
        => Refused(Check(Dev(t => t["index.html"] = t["index.html"].Replace("<link rel=canonical href=https://aiproxydev.click/>", ""))),
            "dev: index.html names no canonical URL");

    [Fact]
    public void TwoHostsInOneBundle_AreRefused()
        => Refused(Check(Dev(t => t["about/index.html"] = Page("scutara.com", "/about/", true))),
            "dev: about/index.html names host scutara.com; index.html names aiproxydev.click");

    [Fact]
    public void AnOpenGraphUrlOnAnotherHost_IsRefused()
        => Refused(Check(Dev(t => t["404.html"] = t["404.html"].Replace("content=\"https://aiproxydev.click/404.html\"", "content=\"https://scutara.com/404.html\""))),
            "dev: 404.html names host scutara.com");

    [Fact]
    public void AnUnindexedEnvironmentsPage_WithoutNoindex_IsRefused()
        => Refused(Check(Dev(t => t["park.html"] = "<!doctype html><html><head><title>Parked</title></head><body></body></html>")),
            "dev: park.html carries no noindex, and dev is not indexed");

    [Fact]
    public void AnIndexedEnvironmentsHome_WithNoindex_IsRefused()
        => Refused(Check(Prod(t => t["index.html"] = Page("scutara.com", "/", noindex: true))),
            "prod: index.html carries noindex, and prod is indexed");

    [Fact]
    public void AnIndexedEnvironment_MayKeepAPageOutOfTheIndex()
        => Assert.True(Check(Prod(t => t["about/index.html"] = Page("scutara.com", "/about/", noindex: true))).Passed);

    [Fact]
    public void ALocalScriptWithoutSri_IsRefused()
        => Refused(Check(Dev(t => { t["js/main.js"] = "1"; t["index.html"] = Page("aiproxydev.click", "/", true, extraHead: "<script src=/js/main.js></script>"); })),
            "dev: index.html loads /js/main.js without SRI");

    [Fact]
    public void ALocalStylesheetWithoutSri_IsRefused()
        => Refused(Check(Dev(t => t["index.html"] = t["index.html"].Replace(" integrity=\"sha256-abc\"", ""))),
            $"dev: index.html loads {Css} without SRI");

    [Fact]
    public void ANamedThirdPartyScript_MayLoadWithoutSri()
        => Assert.True(Check(Dev(t => t["index.html"] = Page("aiproxydev.click", "/", true, extraHead: "<script src=https://link.msgsndr.com/js/form_embed.js></script>"))).Passed);

    [Fact]
    public void AnUnnamedThirdPartyScript_IsRefused()
        => Refused(Check(Dev(t => t["index.html"] = Page("aiproxydev.click", "/", true, extraHead: "<script src=https://cdn.example.net/x.js></script>"))),
            "dev: index.html loads https://cdn.example.net/x.js from cdn.example.net, which is not a named third-party host");

    [Fact]
    public void AnInlineScript_NeedsNoSri()
        => Assert.True(Check(Dev(t => t["index.html"] = Page("aiproxydev.click", "/", true, extraHead: "<script>window.a=1</script>"))).Passed);

    [Fact]
    public void BundlesThatDifferBeyondTheirHost_AreRefusedByFile()
        => Refused(Check(Dev(), Test(t => t["about/index.html"] = t["about/index.html"].Replace("Home", "Start"))),
            "test: about/index.html differs from dev's beyond the host and noindex");

    // A SUBDOMAIN OF THE HOST (V-K step 5, 2026-10-26): the MCP install page and the offer pages name each environment's
    // match.{domain}, so dev's and test's bundles differ there and nowhere else.
    private static Action<Dictionary<string, string>> Naming(string host, string what) => t =>
        t["about/index.html"] = t["about/index.html"].Replace("Home", what.Replace("{host}", host));

    [Fact]
    public void ASubdomainOfEachBundlesHost_IsSetAsideAcrossBundles()
    {
        const string links = "<a href=https://match.{host}/seller/>Get started</a><code>https://match.{host}/mcp</code>" +
                             "<a href=https://a.b.{host}/>Deeper</a>";
        var result = Check(Dev(Naming("aiproxydev.click", links)), Test(Naming("scutara-test.click", links)));
        Assert.True(result.Passed, string.Join("\n", result.Reasons));
    }

    [Fact]
    public void AnotherSubdomain_IsStillADifference()
        => Refused(Check(Dev(Naming("aiproxydev.click", "<a href=https://match.{host}/seller/>Go</a>")),
                         Test(Naming("scutara-test.click", "<a href=https://app.{host}/seller/>Go</a>"))),
            "test: about/index.html differs from dev's beyond the host and noindex");

    [Fact]
    public void AHostThatOnlyEndsLikeTheHost_IsNotSetAside()
        => Refused(Check(Dev(Naming("aiproxydev.click", "<a href=https://evil{host}/>Go</a>")),
                         Test(Naming("scutara-test.click", "<a href=https://evil{host}/>Go</a>"))),
            "test: about/index.html differs from dev's beyond the host and noindex");

    [Fact]
    public void AHostThatOnlyStartsLikeTheHost_IsNotSetAside()
        => Refused(Check(Dev(Naming("aiproxydev.click", "<a href=https://{host}.evil.example/>Go</a>")),
                         Test(Naming("scutara-test.click", "<a href=https://{host}.evil.example/>Go</a>"))),
            "test: about/index.html differs from dev's beyond the host and noindex");

    [Fact]
    public void ASubdomainOutsideAUrl_IsComparedAsWritten()
        => Refused(Check(Dev(Naming("aiproxydev.click", "<p>hi@match.{host}</p>")),
                         Test(Naming("scutara-test.click", "<p>hi@match.{host}</p>"))),
            "test: about/index.html differs from dev's beyond the host and noindex");

    [Fact]
    public void BundlesWithDifferentFiles_AreRefused()
        => Refused(Check(Dev(), Test(t => t["extra.html"] = Page("scutara-test.click", "/extra.html", true))),
            "test: extra.html is not in dev's bundle");

    [Fact]
    public void ABinaryThatDiffers_IsRefused()
    {
        var test = Test();
        test.Files["images/hero.webp"] = new byte[] { 9 };
        Refused(Check(Dev(), test), "test: images/hero.webp differs from dev's");
    }

    [Fact]
    public void TwoBundlesForOneEnvironment_AreRefused()
        => Refused(Check(Dev(), Dev()), "two bundles name environment dev");

    [Fact]
    public void TwoEnvironmentsOnOneHost_AreRefused()
        => Refused(Check(Dev(), new SiteBundle("test", Files("aiproxydev.click", true))), "dev and test both name host aiproxydev.click");

    [Fact]
    public void QuotedAndUnquotedAttributes_ReadAlike()
    {
        var html = "<a href='/x/'>a</a><a href=\"/y/\">b</a><a href=/z/>c</a><A HREF=/w/>d</A>";
        Assert.Equal(new[] { "/x/", "/y/", "/z/", "/w/" }, SiteHtml.Tags(html).Where(t => t.Name == "a").Select(t => t.Attributes["href"]));
    }

    [Fact]
    public void AnEntityInAnAttribute_IsDecoded()
        => Assert.Equal("/a/?x=1&y=2", SiteHtml.Tags("<a href=\"/a/?x=1&amp;y=2\">").Single().Attributes["href"]);

    [Fact]
    public void ACommentIsNotATag()
        => Assert.Empty(SiteHtml.Tags("<!-- <a href=/x/> -->"));
}
