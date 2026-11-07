using System.Text.Json.Nodes;

namespace Lz.Aws.Pipeline;

/// <summary>
/// <b>THE STORES ADMIT THEIR WRITERS ALONE</b> (DecoupledCd.md §12 P3 H, stage H1; BuildWritePath.md). The build account's record and
/// artifact stores refuse an object write from every principal but the ones the plan writes with - the intake's publish function, and
/// each GitHub role not yet retired - and refuse every delete to everyone. An explicit Deny in the resource's own policy, which no
/// Allow in any identity policy outranks, so an administrator's console upload or a CLI put is refused like any other.
///
/// <para>NO EXCEPTION FOR AN EMERGENCY (owner, 2026-11-07): the way past them is to edit the policy, which the edit alarm (H3)
/// reports with its caller. A writer's PREFIX stays its identity policy's to bound: this names who may write at all.</para>
///
/// <para>NOT LIFECYCLE: S3's own expiry of noncurrent versions is not a principal's delete, and `bootstrappipeline` sets the rule.</para>
/// </summary>
public static class StoreWriters
{
    public const string WritersSid = "LzOnlyThePipelineWrites", NoDeletesSid = "LzNoDeletes";

    /// <summary>Deny <c>s3:PutObject</c> on the bucket to every principal not in <paramref name="writerRoleArns"/>.</summary>
    public static JsonObject OnlyTheseWrite(string bucket, IReadOnlyList<string> writerRoleArns)
    {
        if (writerRoleArns.Count == 0)
            throw new InvalidOperationException($"{bucket} would admit no writer: the plan names neither an intake nor a GitHub role that writes it.");
        return new JsonObject
        {
            ["Sid"] = WritersSid,
            ["Effect"] = "Deny",
            ["Principal"] = "*",
            ["Action"] = "s3:PutObject",
            ["Resource"] = $"arn:aws:s3:::{bucket}/*",
            ["Condition"] = new JsonObject
            {
                ["ArnNotEquals"] = new JsonObject
                {
                    // A string for one, as S3 hands a one-element list back.
                    ["aws:PrincipalArn"] = writerRoleArns.Count == 1
                        ? JsonValue.Create(writerRoleArns[0])
                        : new JsonArray(writerRoleArns.OrderBy(a => a, StringComparer.Ordinal).Select(a => (JsonNode?)JsonValue.Create(a)).ToArray()),
                },
            },
        };
    }

    public const string PushersSid = "LzOnlyThePipelinePushes", NoImageDeletesSid = "LzNoImageDeletes";

    /// <summary>The push actions: what a <c>docker push</c> or the intake's publish calls to land an image or its signature.</summary>
    public static readonly string[] PushActions = { "ecr:PutImage", "ecr:InitiateLayerUpload", "ecr:UploadLayerPart", "ecr:CompleteLayerUpload" };

    /// <summary>
    /// THE PIPELINE REPOSITORIES' POLICY (P3 H, stage H2): Deny the push actions to every principal not in
    /// <paramref name="pusherRoleArns"/>, and image and repository deletes to every principal. An AWS SERVICE acting on its own is
    /// exempt from both (<c>aws:PrincipalIsAWSService</c>): ECR's managed signing writes the signature it makes at push, and its
    /// lifecycle policy expires untagged images, neither as a principal of this account. Not <c>PutImageTagMutability</c>: the
    /// bootstrap re-applies immutability every run, and a tag is never what deploys - a digest is.
    /// </summary>
    public static IReadOnlyList<JsonObject> RepositoryStatements(IReadOnlyList<string> pusherRoleArns)
    {
        if (pusherRoleArns.Count == 0)
            throw new InvalidOperationException("a pipeline repository would admit no pusher: the plan names neither an intake nor a GitHub role that pushes.");
        JsonObject NotAService() => new() { ["aws:PrincipalIsAWSService"] = "false" };
        return new[]
        {
            new JsonObject
            {
                ["Sid"] = PushersSid,
                ["Effect"] = "Deny",
                ["Principal"] = "*",
                ["Action"] = new JsonArray(PushActions.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray()),
                ["Condition"] = new JsonObject
                {
                    ["ArnNotEquals"] = new JsonObject
                    {
                        ["aws:PrincipalArn"] = pusherRoleArns.Count == 1
                            ? JsonValue.Create(pusherRoleArns[0])
                            : new JsonArray(pusherRoleArns.OrderBy(a => a, StringComparer.Ordinal).Select(a => (JsonNode?)JsonValue.Create(a)).ToArray()),
                    },
                    ["BoolIfExists"] = NotAService(),
                },
            },
            new JsonObject
            {
                ["Sid"] = NoImageDeletesSid,
                ["Effect"] = "Deny",
                ["Principal"] = "*",
                ["Action"] = new JsonArray("ecr:BatchDeleteImage", "ecr:DeleteRepository"),
                ["Condition"] = new JsonObject { ["BoolIfExists"] = NotAService() },
            },
        };
    }

    /// <summary>Deny every object delete on the bucket, to everyone.</summary>
    public static JsonObject NoDeletes(string bucket) => new()
    {
        ["Sid"] = NoDeletesSid,
        ["Effect"] = "Deny",
        ["Principal"] = "*",
        ["Action"] = new JsonArray("s3:DeleteObject", "s3:DeleteObjectVersion"),
        ["Resource"] = $"arn:aws:s3:::{bucket}/*",
    };
}

/// <summary>A pipeline repository's policy, merged by Sid like a store's bucket policy (P3 H2), then read back.</summary>
public static class RepositoryPolicies
{
    public static async Task MergeAsync(Amazon.ECR.IAmazonECR ecr, string repository, IReadOnlyList<JsonObject> owned)
    {
        async Task<string?> ReadAsync()
        {
            try
            {
                return (await ecr.GetRepositoryPolicyAsync(new Amazon.ECR.Model.GetRepositoryPolicyRequest { RepositoryName = repository })).PolicyText;
            }
            catch (Amazon.ECR.Model.RepositoryPolicyNotFoundException)
            {
                return null;
            }
        }

        var existing = await ReadAsync();
        if (WriteOnceStore.StatementsNotHeld(existing, owned).Count == 0)
        {
            Console.WriteLine($"  repository policy on '{repository}' already holds {string.Join(", ", owned.Select(o => o["Sid"]!.GetValue<string>()))}.");
            return;
        }
        await ecr.SetRepositoryPolicyAsync(new Amazon.ECR.Model.SetRepositoryPolicyRequest
        {
            RepositoryName = repository,
            PolicyText = CrossAccount.MergeBySid(existing, owned),
        });
        var missing = WriteOnceStore.StatementsNotHeld(await ReadAsync(), owned);
        if (missing.Count > 0)
            throw new InvalidOperationException($"repository policy on '{repository}' was written but does not hold {string.Join(", ", missing)} when read back.");
        Console.WriteLine($"  repository policy on '{repository}' written and read back: {string.Join(", ", owned.Select(o => o["Sid"]!.GetValue<string>()))}.");
    }
}
