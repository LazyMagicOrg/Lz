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
