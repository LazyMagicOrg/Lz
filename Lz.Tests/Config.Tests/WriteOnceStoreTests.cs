using System.Text.Json.Nodes;
using Lz.Aws.Pipeline;
using Lz.Tests.Build.Tests;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// Write-once, enforced by the stores (DecoupledCd.md §14.3): the statement, the read-back that says a policy holds it,
/// and where both bootstraps apply it. The statement's form is AWS's ("Enforce conditional writes on Amazon S3
/// buckets"); what these pin is that it stays that form, admits no overwrite, and reaches every store before a writer.
/// </summary>
public class WriteOnceStoreTests
{
    private const string Bucket = "scu-build-records-4df6-b9c6";

    // ---------------------------------------------------------------------------------------
    //  The statement
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void TheStatement_DeniesEveryoneAnObjectWriteThatDoesNotAskToFailWhenTheKeyExists()
    {
        var expected = JsonNode.Parse($$"""
            {
              "Sid": "LzWriteOnce",
              "Effect": "Deny",
              "Principal": "*",
              "Action": "s3:PutObject",
              "Resource": "arn:aws:s3:::{{Bucket}}/*",
              "Condition": {
                "Null": { "s3:if-none-match": "true" },
                "Bool": { "s3:ObjectCreationOperation": "true" }
              }
            }
            """);

        Assert.True(JsonNode.DeepEquals(expected, WriteOnceStore.Deny(Bucket)), WriteOnceStore.Deny(Bucket).ToJsonString());
    }

    [Fact]
    public void AnOverwriteByETag_IsNotAdmitted()
    {
        // AWS's third example admits If-Match as well, which replaces an object whose ETag matches. Adding it here would
        // look like a harmless widening and would re-open exactly the overwrite this exists to close.
        Assert.DoesNotContain("if-match", WriteOnceStore.Deny(Bucket).ToJsonString(), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------
    //  Reading it back
    // ---------------------------------------------------------------------------------------

    private static readonly IReadOnlyList<JsonObject> ReadGrant =
        CrossAccount.BuildRecordReadGrant(Bucket, "dev", "503947800380", "scu-dev-deployer-verify-fn", "scu-dev-deployer-start-fn");

    [Fact]
    public void APolicyHoldsTheStatements_InS3sFormatting_AmongOthers()
    {
        var owned = new[] { WriteOnceStore.Deny(Bucket) };

        // Compact, with the statement's properties in another order, beside a statement that is not ours.
        var readBack = """
            {"Version":"2012-10-17","Statement":[
              {"Sid":"SomeoneElses","Effect":"Allow","Principal":{"AWS":"arn:aws:iam::111111111111:root"},"Action":"s3:GetObject","Resource":"arn:aws:s3:::scu-build-records-4df6-b9c6/*"},
              {"Condition":{"Bool":{"s3:ObjectCreationOperation":"true"},"Null":{"s3:if-none-match":"true"}},"Resource":"arn:aws:s3:::scu-build-records-4df6-b9c6/*","Action":"s3:PutObject","Principal":"*","Effect":"Deny","Sid":"LzWriteOnce"}]}
            """;

        Assert.Empty(WriteOnceStore.StatementsNotHeld(readBack, owned));

        // A policy may hold its one statement as an object rather than an array.
        var single = new JsonObject { ["Version"] = "2012-10-17", ["Statement"] = WriteOnceStore.Deny(Bucket) };
        Assert.Empty(WriteOnceStore.StatementsNotHeld(single.ToJsonString(), owned));
    }

    [Fact]
    public void APolicyThatLacksOrAltersAStatement_DoesNotHoldIt()
    {
        var owned = new[] { WriteOnceStore.Deny(Bucket) };

        Assert.Equal(new[] { WriteOnceStore.Sid }, WriteOnceStore.StatementsNotHeld(null, owned));
        Assert.Equal(new[] { WriteOnceStore.Sid }, WriteOnceStore.StatementsNotHeld("""{"Version":"2012-10-17","Statement":[]}""", owned));
        Assert.Equal(new[] { WriteOnceStore.Sid }, WriteOnceStore.StatementsNotHeld("<Error><Code>NoSuchBucketPolicy</Code></Error>", owned));

        // Our Sid, widened to admit an overwrite by ETag: not the statement that was written.
        var widened = WriteOnceStore.Deny(Bucket);
        widened["Condition"]!["Null"]!["s3:if-match"] = "true";
        var policy = new JsonObject { ["Version"] = "2012-10-17", ["Statement"] = new JsonArray(widened) };
        Assert.Equal(new[] { WriteOnceStore.Sid }, WriteOnceStore.StatementsNotHeld(policy.ToJsonString(), owned));
    }

    [Fact]
    public void TheRecordStoresTwoMerges_KeepEachOther()
    {
        // The record store's policy is written twice in one run: write-once when the store is ensured, and the
        // environment's grant later. Each merge must carry the other's statements over, in either order.
        var deny = new[] { WriteOnceStore.Deny(Bucket) };
        var everything = deny.Concat(ReadGrant).ToList();

        var denyFirst = CrossAccount.MergeBySid(CrossAccount.MergeBySid(null, deny), ReadGrant);
        var grantFirst = CrossAccount.MergeBySid(CrossAccount.MergeBySid(null, ReadGrant), deny);

        Assert.Empty(WriteOnceStore.StatementsNotHeld(denyFirst, everything));
        Assert.Empty(WriteOnceStore.StatementsNotHeld(grantFirst, everything));

        // And a re-run replaces rather than accumulates.
        var again = CrossAccount.MergeBySid(denyFirst, deny);
        Assert.Equal(3, JsonNode.Parse(again)!["Statement"]!.AsArray().Count);
    }

    // ---------------------------------------------------------------------------------------
    //  Where it is applied — the plans' tests cannot see whether the bootstraps act on them
    // ---------------------------------------------------------------------------------------

    private static string Source(params string[] path)
    {
        var file = Path.Combine(new[] { PackageHandlingScratchBuild.FindLzRepoRoot() }.Concat(path).ToArray());
        Assert.True(File.Exists(file), $"source not found at {file}");
        return File.ReadAllText(file);
    }

    private static int At(string src, string call, string file)
    {
        var i = src.IndexOf(call, StringComparison.Ordinal);
        Assert.True(i >= 0, $"{file} no longer contains {call}");
        return i;
    }

    [Fact]
    public void TheBuildAccountBootstrap_MakesEachStoreWriteOnce_BeforeAnyRoleThatCouldWrite()
    {
        const string file = "PipelineBootstrapper.cs";
        var src = Source("Lz.Aws", "Pipeline", file);

        var ensure = At(src, "await EnsureStoreAsync(s3, store, region);", file);
        var writeOnce = At(src, "await BucketPolicies.MergeAsync(s3, store.Name, store.PolicyStatements);", file);
        var roles = At(src, "await EnsureRoleAsync(iam, role, providerArn);", file);

        Assert.True(ensure < writeOnce && writeOnce < roles,
            "the write-once policy is no longer applied between creating each store and creating the roles GitHub assumes");

        // The environment's grant goes through the same read-merge-write, so neither merge drops the other.
        At(src, "await BucketPolicies.MergeAsync(s3, recordStore, grant);", file);
    }

    [Fact]
    public void TheDeployerBootstrap_MakesTheEvidenceStoreWriteOnce_BeforeTheFunctionsThatWriteIt()
    {
        const string file = "DeployerBootstrapper.cs";
        var src = Source("Lz.Aws", "Pipeline", file);

        var ensure = At(src, "await EnsureEvidenceStoreAsync(s3, plan.EvidenceStore, region);", file);
        var writeOnce = At(src, "await BucketPolicies.MergeAsync(s3, plan.EvidenceStore, plan.EvidenceStorePolicy);", file);
        var functionRoles = At(src, "var fnRole = await EnsureRoleAsync(iam, fn.RoleName,", file);

        Assert.True(ensure < writeOnce && writeOnce < functionRoles,
            "the evidence store's write-once policy is no longer applied before the roles of the functions that write evidence");
    }

    [Fact]
    public void AMergedPolicy_IsReadBack_AndAPolicyThatDoesNotHoldItFailsTheRun()
    {
        const string file = "WriteOnceStore.cs";
        var src = Source("Lz.Aws", "Pipeline", file);
        var body = src[At(src, "public static async Task MergeAsync(", file)..];

        var put = At(body, "await s3.PutBucketPolicyAsync(", file);
        var readBack = At(body, "var missing = WriteOnceStore.StatementsNotHeld(await ReadAsync(s3, bucket), owned);", file);
        const string check = "if (missing.Count > 0)";
        var refusal = At(body, check, file);

        Assert.True(put < readBack && readBack < refusal, "the policy is no longer read back after it is written");
        Assert.StartsWith("throw new InvalidOperationException(", body[(refusal + check.Length)..].TrimStart());
    }

    [Fact]
    public void TheRecordReader_ReturnsTheVersionOfTheBytesItRead()
    {
        const string file = "Adapters.cs";
        var src = Source("Lz.Aws.Deployer", file);

        At(src, "return new StoredRecord(await reader.ReadToEndAsync(), response.VersionId);", file);
    }
}
