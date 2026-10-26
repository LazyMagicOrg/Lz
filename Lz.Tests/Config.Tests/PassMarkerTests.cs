using System.Text.Json.Nodes;
using Lz.Aws.Pipeline;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// The pass marker (DecoupledCd.md §12 P5 stage C, T-12, T-13): what an environment writes, after its Record and its
/// Gate 2, to say the SET it ran passed - the Service digest and each client's bundle version, each with its record and its
/// deploy evidence - at <c>passed/sets/{sha256 of the member identities}.json</c> in its own evidence store, for the next
/// environment to read. Its parser fails closed: anything missing, unversioned, unexpected or for another environment is
/// refused with the field it failed on.
/// </summary>
public class PassMarkerTests
{
    private const string Records = "scu-build-records-4df6-b9c6";

    private static PassMarkerMember Image(string digest = "sha256:" + "a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f90") => new(
        Class: "image", Repo: "Scutara/ScutaraService",
        Record: new PassMarkerRecordReference(Records, "image/scutara/scutaraservice/20261025T132455Z-40103719752-1.json", "rec-v1"),
        Identity: new BuildRecordIdentity("image", Digest: digest),
        Version: "1.4.212",
        BuiltFrom: new BuildRecordBuiltFrom("Scutara/ScutaraService", new string('1', 40), "published", new Dictionary<string, string>(), "refs/heads/main"),
        Deploy: new PassMarkerReference("deploys/req-20261025T132600Z-40103719752-mp-1.json", "dep-v1"));

    private static PassMarkerMember Client(string app = "sellerapp", string version = "bun-v7") => new(
        Class: "client", Repo: $"Scutara/Scutara{char.ToUpperInvariant(app[0])}{app[1..^3]}App",
        Record: new PassMarkerRecordReference(Records, $"client/scutara/{app}/20261024T101010Z-40000000001-1.json", "rec-v2"),
        Identity: new BuildRecordIdentity("bundle", Bucket: "scu-artifacts-4df6-b9c6", Key: $"client/scutara/{app}/20261024T101010Z-40000000001-1.zip", VersionId: version, Sha256: new string('b', 64)),
        Version: version,
        BuiltFrom: new BuildRecordBuiltFrom($"Scutara/{app}", new string('2', 40), "published", new Dictionary<string, string> { ["AipApi"] = "1.4.210" }, "refs/heads/main"),
        Deploy: new PassMarkerReference($"deploys/req-20261024T101100Z-40000000001-{app}-1.json", "dep-v2"));

    private static PassMarker Marker(params PassMarkerMember[] members)
    {
        var m = members.Length == 0 ? new[] { Image(), Client(), Client("adminapp", "bun-v3") } : members;
        return new PassMarker(
            Schema: 1, MarkerId: PassMarkerFormat.MarkerIdFor(m), Environment: "dev", Members: m,
            Evidence: new PassMarkerEvidence(new PassMarkerReference("tests/req-20261025T132600Z-40103719752-mp-1.json", "tst-v1")),
            Execution: "req-20261025T132600Z-40103719752-mp-1", WrittenAt: "2026-10-25T13:40:00Z");
    }

    private static string Refusal(string json, string environment = "dev")
        => Assert.Throws<InvalidOperationException>(() => PassMarkerFormat.Parse(json, environment)).Message;

    /// <summary>The marker as JSON with one change made to it.</summary>
    private static string With(Action<JsonObject> change, PassMarker? marker = null)
    {
        var node = JsonNode.Parse(PassMarkerFormat.Write(marker ?? Marker()))!.AsObject();
        change(node);
        return node.ToJsonString();
    }

    private static JsonObject Member0(JsonObject m) => m["members"]![0]!.AsObject();

    // ---- what it is ----

    [Fact]
    public void AMarker_RoundTrips()
    {
        var marker = Marker();
        var parsed = PassMarkerFormat.Parse(PassMarkerFormat.Write(marker), "dev");
        Assert.Equal(PassMarkerFormat.Write(marker), PassMarkerFormat.Write(parsed));
        Assert.Equal(marker.MarkerId, parsed.MarkerId);
        Assert.Equal(3, parsed.Members.Count);
    }

    [Fact]
    public void ItsKeys_AreTheSetsHash_UnderPassedAndDecisions()
    {
        var id = Marker().MarkerId;
        Assert.Matches("^sets/[0-9a-f]{64}$", id);
        Assert.Equal($"passed/{id}.json", PassMarkerFormat.KeyFor(id));
        Assert.Equal($"decisions/{id}.json", PassMarkerFormat.DecisionKeyFor(id));
    }

    [Fact]
    public void TheSameMembersInAnotherOrder_GiveTheSameMarkerId()
        => Assert.Equal(PassMarkerFormat.MarkerIdFor(new[] { Image(), Client(), Client("adminapp", "bun-v3") }),
                        PassMarkerFormat.MarkerIdFor(new[] { Client("adminapp", "bun-v3"), Image(), Client() }));

    [Fact]
    public void ADifferentMember_GivesADifferentMarkerId()
    {
        var a = PassMarkerFormat.MarkerIdFor(new[] { Image(), Client() });
        Assert.NotEqual(a, PassMarkerFormat.MarkerIdFor(new[] { Image(), Client(version: "bun-v8") }));
        Assert.NotEqual(a, PassMarkerFormat.MarkerIdFor(new[] { Image("sha256:" + new string('f', 64)), Client() }));
        Assert.NotEqual(a, PassMarkerFormat.MarkerIdFor(new[] { Image() }));
    }

    [Fact]
    public void TheMarkerId_IsTheMembersOnly_NotWhoWroteItOrWhen()
    {
        // A second pass of the same set meets the write-once statement under the same key: it counts as already passed.
        var first = Marker();
        var second = first with { Execution = "req-other-1", WrittenAt = "2026-10-26T00:00:00Z" };
        Assert.Equal(first.MarkerId, PassMarkerFormat.MarkerIdFor(second.Members));
    }

    // ---- what the parser refuses ----

    [Theory]
    [InlineData("schema")]
    [InlineData("markerId")]
    [InlineData("environment")]
    [InlineData("members")]
    [InlineData("evidence")]
    [InlineData("execution")]
    [InlineData("writtenAt")]
    public void AMissingField_IsRefusedByName(string field)
        => Assert.Contains(field, Refusal(With(m => m.Remove(field))));

    [Theory]
    [InlineData("class")]
    [InlineData("repo")]
    [InlineData("record")]
    [InlineData("identity")]
    [InlineData("version")]
    [InlineData("builtFrom")]
    public void AMemberMissingAField_IsRefusedByName(string field)
        => Assert.Contains(field, Refusal(With(m => Member0(m).Remove(field))));

    [Fact]
    public void AMemberWithoutDeployEvidence_IsRefused()
        // It reached the environment outside the pipeline, so nothing here vouches for it (T-13).
        => Assert.Contains("deploy", Refusal(With(m => Member0(m).Remove("deploy"))));

    [Fact]
    public void AMarkerForAnotherEnvironment_IsRefused()
    {
        var message = Refusal(PassMarkerFormat.Write(Marker()), environment: "test");
        Assert.Contains("'dev'", message);
        Assert.Contains("'test'", message);
    }

    [Theory]
    [InlineData("record")]
    [InlineData("deploy")]
    public void AMembersReferenceWithoutAVersion_IsRefused(string reference)
        => Assert.Contains($"{reference}.versionId", Refusal(With(m => Member0(m)[reference]!.AsObject().Remove("versionId"))));

    [Fact]
    public void TheTestEvidenceWithoutAVersion_IsRefused()
        => Assert.Contains("evidence.tests.versionId", Refusal(With(m => m["evidence"]!["tests"]!.AsObject().Remove("versionId"))));

    [Fact]
    public void AMarkerIdThatIsNotItsMembersHash_IsRefused()
        => Assert.Contains("markerId", Refusal(With(m => m["markerId"] = "sets/" + new string('0', 64))));

    [Fact]
    public void AnotherSchema_IsRefused()
        => Assert.Contains("schema 2", Refusal(With(m => m["schema"] = 2)));

    [Theory]
    [InlineData("toolingDigest")]
    [InlineData("configBundleVersion")]
    public void ToolingDigestOrConfigBundleVersion_IsRefused_WhileNoMemberIsOfClasses5To7(string field)
    {
        // Not merely an unknown field: the refusal says when it becomes one, so the day classes 5 to 7 deploy it is found.
        var message = Refusal(With(m => m[field] = "x"));
        Assert.Contains(field, message);
        Assert.Contains("classes 5 to 7", message);
    }

    [Fact]
    public void AnUnknownField_IsRefused_SinceTheParserFailsClosed()
        => Assert.Contains("approvedBy", Refusal(With(m => m["approvedBy"] = "someone")));

    [Fact]
    public void AMemberOfAClassAMarkerNeverNames_IsRefused()
        // The site is never promoted (V-24); a marker names the image and the clients.
        => Assert.Contains("'site'", Refusal(With(m => Member0(m)["class"] = "site")));

    [Fact]
    public void NoMembers_OrTheSameMemberTwice_IsRefused()
    {
        Assert.Contains("members", Refusal(With(m => m["members"] = new JsonArray())));
        Assert.Contains("twice", Assert.Throws<InvalidOperationException>(() => PassMarkerFormat.MarkerIdFor(new[] { Image(), Image() })).Message);
    }

    [Fact]
    public void AnImageMemberNamedByAnythingButADigest_IsRefused()
        => Assert.Contains("identity", Refusal(With(m => Member0(m)["identity"]!["digest"] = "latest")));

    [Fact]
    public void ABundleMemberWithoutItsObjectVersion_IsRefused()
        => Assert.Contains("identity.versionId",
            Refusal(With(m => m["members"]![1]!["identity"]!.AsObject().Remove("versionId"), Marker(Image(), Client()))));

    [Fact]
    public void NotJson_IsRefused()
        => Assert.Contains("JSON", Refusal("{ not json"));
}
