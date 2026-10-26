using System.Text.Json.Nodes;

namespace Lz.Aws.Pipeline;

/// <summary>
/// One member a marker may name, as this environment's Gate 2 reads it: an image by its service's <c>cluster/service</c>
/// key in <see cref="Gate2Running.Digests"/>, a client by its app in <see cref="Gate2Running.Bundles"/>, with the repository
/// whose deploy evidence it is traced to.
/// </summary>
public sealed record MarkMember(string Class, string Repo, string Key);

/// <summary>MarkPassed's settings: the environment it writes for, its evidence store, and the members its set may name.</summary>
public sealed record MarkPassedSettings(string Environment, string EvidenceStore, IReadOnlyList<MarkMember> Members);

/// <summary>No marker for this set: a refusal, recorded by the execution's failure and alerted; never retried.</summary>
public sealed class MarkPassedRefused(string message) : Exception(message);

/// <summary>
/// MARKPASSED (DecoupledCd.md §12 P5 stage D, T-12, T-13), after Gate 2 passed, on the success branch only: the set Gate 2
/// found running - each service's one digest and each app's bundle version, the site apart - traced, member by member, to
/// the newest deploy evidence in this environment that landed it, and written once as a pass marker at
/// <c>passed/sets/{sha256}.json</c> for the next environment to read. Each member's record, identity and builtFrom are
/// copied from that evidence, as Verify read them from the record; the evidence and the set's tests are named at their
/// versions. A member with no such evidence reached the environment outside the pipeline, and no marker is written.
/// A second pass of the same set meets the write-once statement and counts as already passed.
/// </summary>
public static class MarkPassedStep
{
    /// <summary>How far back a member's deploy evidence is looked for: the newest this many under its repository.</summary>
    public const int EvidenceSearchLimit = 100;

    public static async Task<JsonObject> RunAsync(JsonObject state, string executionName, MarkPassedSettings settings,
        IGate2EvidenceReader keys, IRecordStore reads, IEvidenceWriter writer, DateTimeOffset now)
    {
        var gate2 = state["gate2"] as JsonObject ?? throw new MarkPassedRefused("the state has no Gate 2 result.");
        if (gate2["outcome"]?.GetValue<string>() != "Passed")
            throw new MarkPassedRefused($"Gate 2 ended {gate2["outcome"]}, not Passed; only a passed set is marked.");
        var before = state["gate2Start"]?["before"] as JsonObject ?? throw new MarkPassedRefused("the state has no Gate 2 reading of what ran.");

        var members = new List<PassMarkerMember>();
        foreach (var m in settings.Members)
        {
            if (m.Class == "image")
            {
                var digests = (before["digests"]?[m.Key] as JsonArray ?? new JsonArray()).Select(d => d?.GetValue<string>()).Distinct().ToList();
                if (digests.Count != 1 || digests[0] is not { } digest)
                    throw new MarkPassedRefused($"{m.Key} ran {digests.Count} digests while Gate 2 read it ({string.Join(", ", digests)}); a set names one.");
                members.Add(await MemberAsync(m, settings, keys, reads, v => v["digest"]?.GetValue<string>() == digest,
                    v => new BuildRecordIdentity("image", Digest: digest), v => v["serviceVersion"]?.GetValue<string>() ?? digest));
            }
            else
            {
                // AN APP SERVING NOTHING is no member: there is nothing of it to promote.
                if (before["bundles"]?[m.Key]?.GetValue<string>() is not { } version) continue;
                members.Add(await MemberAsync(m, settings, keys, reads, v => v["identity"]?["versionId"]?.GetValue<string>() == version,
                    v => new BuildRecordIdentity("bundle", Bucket: v["identity"]!["bucket"]!.GetValue<string>(), Key: v["identity"]!["key"]!.GetValue<string>(),
                        VersionId: version, Sha256: v["identity"]!["sha256"]!.GetValue<string>()), _ => version));
            }
        }

        var testsKey = gate2["evidence"]?.GetValue<string>() ?? throw new MarkPassedRefused("Gate 2's result names no tests evidence.");
        var tests = await reads.ReadAsync(settings.EvidenceStore, testsKey);
        if (tests?.VersionId is not { Length: > 0 } testsVersion || testsVersion == "null")
            throw new MarkPassedRefused($"{testsKey} could not be read at a version.");

        var marker = new PassMarker(PassMarkerFormat.CurrentSchema, PassMarkerFormat.MarkerIdFor(members), settings.Environment, members,
            new PassMarkerEvidence(new PassMarkerReference(testsKey, testsVersion)), executionName, now.ToString("O"));
        var key = PassMarkerFormat.KeyFor(marker.MarkerId);
        var written = await writer.PutOnceAsync(settings.EvidenceStore, key, PassMarkerFormat.Write(marker));
        return new JsonObject { ["markerId"] = marker.MarkerId, ["key"] = key, ["written"] = written, ["members"] = members.Count };
    }

    private static async Task<PassMarkerMember> MemberAsync(MarkMember m, MarkPassedSettings settings, IGate2EvidenceReader keys, IRecordStore reads,
        Func<JsonObject, bool> isIt, Func<JsonObject, BuildRecordIdentity> identity, Func<JsonObject, string> version)
    {
        var prefix = $"deploys/{BuildRecordFormat.PrefixFor(m.Class, m.Repo)}";
        foreach (var key in (await keys.KeysAsync(settings.EvidenceStore, prefix)).OrderByDescending(k => k, StringComparer.Ordinal).Take(EvidenceSearchLimit))
        {
            var stored = await reads.ReadAsync(settings.EvidenceStore, key);
            if (stored is null || JsonNode.Parse(stored.Json) is not JsonObject deploy) continue;
            if (deploy["outcome"]?.GetValue<string>() != "deployed" || deploy["verified"] is not JsonObject verified || !isIt(verified)) continue;
            if (stored.VersionId is not { Length: > 0 } deployVersion || deployVersion == "null")
                throw new MarkPassedRefused($"{key} could not be read at a version.");

            var record = deploy["record"] as JsonObject ?? throw new MarkPassedRefused($"{key} names no record.");
            var built = verified["builtFrom"] as JsonObject ?? throw new MarkPassedRefused($"{key} names no builtFrom.");
            var builtFrom = new BuildRecordBuiltFrom(
                built["repo"]!.GetValue<string>(), built["commit"]!.GetValue<string>(), built["lane"]!.GetValue<string>(),
                (built["packages"] as JsonObject ?? new JsonObject()).ToDictionary(p => p.Key, p => p.Value!.GetValue<string>()),
                built["ref"]?.GetValue<string>());
            return new PassMarkerMember(m.Class, m.Repo,
                new PassMarkerRecordReference(record["bucket"]!.GetValue<string>(), record["key"]!.GetValue<string>(), verified["recordVersionId"]!.GetValue<string>()),
                identity(verified), version(verified), builtFrom, new PassMarkerReference(key, deployVersion));
        }
        throw new MarkPassedRefused(
            $"{m.Repo}'s {m.Key} runs what no deploy evidence under {prefix} landed: it reached {settings.Environment} outside the pipeline, " +
            "and a set with such a member is not promoted.");
    }
}
