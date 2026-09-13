using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.S3;
using Amazon.S3.Model;

namespace Lz.Aws.Pipeline;

/// <summary>
/// <b>WRITE-ONCE, ENFORCED BY THE STORE</b> (DecoupledCd.md §14.3). Every store the pipeline keeps — the build account's
/// artifact, build-record and request stores, and each environment's evidence store — refuses an object write that does
/// not ask S3 to fail when the key already exists.
///
/// <para>WHY THE STORE AND NOT THE WRITERS. Every grant of <c>s3:PutObject</c> is unconditional, a build role's and the
/// Record function's alike. The conditional write lived only in the writers — <c>--if-none-match "*"</c> in the build
/// workflow, <c>IfNoneMatch</c> in the evidence writer — so any branch of a trusted repository could assume its role and
/// write a new current version at an existing record key, and Verify reads the current version. This makes the
/// condition the store's, whoever writes.</para>
///
/// <para>THE FORM IS AWS's, from "Enforce conditional writes on Amazon S3 buckets": deny <c>s3:PutObject</c> when
/// <c>s3:if-none-match</c> is absent (<c>Null</c> true) and <c>s3:ObjectCreationOperation</c> is true. The second key is
/// what lets a multipart upload through: <c>CreateMultipartUpload</c>, <c>UploadPart</c> and <c>UploadPartCopy</c> take
/// no conditional header and are exempt, and the upload's <c>CompleteMultipartUpload</c> must carry one. Only
/// <c>If-None-Match</c> is admitted. AWS's example admits <c>If-Match</c> too, which overwrites an object whose ETag
/// matches, and an overwrite is what this exists to prevent.</para>
///
/// <para>WHAT ELSE IT REFUSES, per the same page: <c>CopyObject</c> into the store — 403 without a conditional header,
/// 501 with one. Nothing copies into these stores. WHAT IT DOES NOT: in a versioned bucket <c>If-None-Match</c> succeeds
/// when the key's current version is a delete marker, so a key can gain a second version after a delete — and no build
/// role or deployer function may delete.</para>
///
/// <para>PRINCIPAL <c>*</c>, NOT A ROLE LIST: it binds every writer, an administrator's console upload included.</para>
/// </summary>
public static class WriteOnceStore
{
    public const string Sid = "LzWriteOnce";

    /// <summary>The statement, for one bucket. Merged by <see cref="Sid"/>, so every run rewrites the same one.</summary>
    public static JsonObject Deny(string bucket) => new()
    {
        ["Sid"] = Sid,
        ["Effect"] = "Deny",
        ["Principal"] = "*",
        ["Action"] = "s3:PutObject",
        ["Resource"] = $"arn:aws:s3:::{bucket}/*",
        ["Condition"] = new JsonObject
        {
            ["Null"] = new JsonObject { ["s3:if-none-match"] = "true" },
            ["Bool"] = new JsonObject { ["s3:ObjectCreationOperation"] = "true" },
        },
    };

    /// <summary>
    /// The Sids of <paramref name="owned"/> that <paramref name="policy"/> does not hold exactly — absent, or present with
    /// different content. Empty when it holds them all. Compared as JSON, statement by statement, so a policy S3 hands back
    /// in its own formatting, or with other statements around ours, still holds them.
    /// </summary>
    public static IReadOnlyList<string> StatementsNotHeld(string? policy, IReadOnlyList<JsonObject> owned)
    {
        var held = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(policy))
        {
            JsonNode? statements;
            try
            {
                statements = JsonNode.Parse(policy)?["Statement"];
            }
            catch (JsonException)
            {
                statements = null;
            }

            var list = statements switch
            {
                JsonArray array => array.ToList(),
                JsonObject single => new List<JsonNode?> { single },
                _ => new List<JsonNode?>(),
            };

            foreach (var statement in list.OfType<JsonObject>())
            {
                if (statement["Sid"] is JsonValue sid && sid.TryGetValue<string>(out var name))
                    held.TryAdd(name, statement);
            }
        }

        var notHeld = new List<string>();
        foreach (var statement in owned)
        {
            var sid = statement["Sid"]!.GetValue<string>();
            if (!held.TryGetValue(sid, out var found) || !JsonNode.DeepEquals(found, statement))
                notHeld.Add(sid);
        }

        return notHeld;
    }
}

/// <summary>
/// Merges owned statements into a bucket's one policy by Sid and reads it back — for the write-once statement on every
/// store and the build-record store's grant to each environment. The policy is replaced whole by its Put, so everything
/// not owned is carried over (<see cref="CrossAccount.MergeBySid"/>).
/// </summary>
internal static class BucketPolicies
{
    public static async Task MergeAsync(IAmazonS3 s3, string bucket, IReadOnlyList<JsonObject> owned)
    {
        var sids = string.Join(", ", owned.Select(o => o["Sid"]!.GetValue<string>()));

        var existing = await ReadAsync(s3, bucket);
        if (WriteOnceStore.StatementsNotHeld(existing, owned).Count == 0)
        {
            Console.WriteLine($"  bucket policy on '{bucket}' already holds {sids}.");
            return;
        }

        var merged = CrossAccount.MergeBySid(existing, owned);
        await s3.PutBucketPolicyAsync(new PutBucketPolicyRequest { BucketName = bucket, Policy = merged });

        // READ BACK, because the put answers only that S3 accepted a document.
        var missing = WriteOnceStore.StatementsNotHeld(await ReadAsync(s3, bucket), owned);
        if (missing.Count > 0)
            throw new InvalidOperationException(
                $"the bucket policy on '{bucket}' was written but does not read back holding {string.Join(", ", missing)} as written.");

        var total = JsonNode.Parse(merged)!["Statement"]!.AsArray().Count;
        Console.WriteLine($"  bucket policy on '{bucket}' written and read back: {sids}; {total - owned.Count} other statement(s) preserved.");
    }

    private static async Task<string?> ReadAsync(IAmazonS3 s3, string bucket)
    {
        // THE SDK REPORTS "NO POLICY" AS A 404 RESPONSE WITH THE ERROR XML IN Policy, not as an exception (measured on
        // the build account's first apply); ExistingBucketPolicy decides that. The catch stays for an SDK that raises
        // instead, and matches only that one absence.
        try
        {
            var response = await s3.GetBucketPolicyAsync(new GetBucketPolicyRequest { BucketName = bucket });
            return CrossAccount.ExistingBucketPolicy(response.HttpStatusCode, response.Policy);
        }
        catch (AmazonS3Exception ex) when (ex.ErrorCode == "NoSuchBucketPolicy")
        {
            return null;
        }
    }
}
