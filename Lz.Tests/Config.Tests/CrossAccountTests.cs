using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.ECR;
using Amazon.ECR.Model;
using Lz.Aws.Pipeline;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// What crosses between the build account and a target account: replication, the replication
/// permission, and the Verify function's read of build records (MigrationPlan M5).
///
/// <para>MOST OF THESE ARE ABOUT WHAT IS PRESERVED. A registry's replication configuration, a registry
/// policy and a bucket policy are each replaced whole by their Put call, and one run owns only its own
/// environment's part. The failure these guard against is the Route 53 one: a write that meant to add
/// one thing and silently deleted what was already there.</para>
/// </summary>
public class CrossAccountTests
{
    private const string Build = "147440642635";
    private const string Dev = "503947800380";
    private const string Prod = "982408502448";
    private const string Store = "scu-build-records-4df6-b9c6";
    private static readonly string[] Repos = { "scu-4df6-b9c6-aiphost" };

    private static JsonObject Statement(string sid, string action = "s3:GetObject") => new()
    {
        ["Sid"] = sid, ["Effect"] = "Allow", ["Principal"] = new JsonObject { ["AWS"] = "arn:aws:iam::1:root" },
        ["Action"] = action, ["Resource"] = "*",
    };

    private static List<JsonElement> Statements(string policy)
        => JsonDocument.Parse(policy).RootElement.GetProperty("Statement").EnumerateArray().ToList();

    // ---------------------------------------------------------------------------------------
    //  Merging a policy by Sid
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void IntoNoPolicy_TheOwnedStatementsAreTheWholePolicy()
    {
        var merged = CrossAccount.MergeBySid(null, new[] { Statement("A") });

        Assert.Equal("2012-10-17", JsonDocument.Parse(merged).RootElement.GetProperty("Version").GetString());
        Assert.Equal("A", Assert.Single(Statements(merged)).GetProperty("Sid").GetString());
    }

    [Fact]
    public void EveryOtherStatement_IsPreservedExactly()
    {
        // THE ONE THAT MATTERS. Another environment's grant, or one written by hand, must survive a run
        // that was only ever about this environment.
        var existing = CrossAccount.MergeBySid(null, new[] { Statement("ProdGrant", "s3:GetObjectVersion"), Statement("Handwritten", "s3:ListBucket") });

        var merged = CrossAccount.MergeBySid(existing, new[] { Statement("DevGrant") });

        var bySid = Statements(merged).ToDictionary(s => s.GetProperty("Sid").GetString()!);
        Assert.Equal(new[] { "DevGrant", "Handwritten", "ProdGrant" }, bySid.Keys.OrderBy(k => k));
        Assert.Equal("s3:GetObjectVersion", bySid["ProdGrant"].GetProperty("Action").GetString());
        Assert.Equal("s3:ListBucket", bySid["Handwritten"].GetProperty("Action").GetString());
    }

    [Fact]
    public void AStatementWithTheSameSid_IsReplaced_NotDuplicated()
    {
        var first = CrossAccount.MergeBySid(null, new[] { Statement("DevGrant", "s3:GetObject") });

        var second = CrossAccount.MergeBySid(first, new[] { Statement("DevGrant", "s3:GetObjectAcl") });

        var only = Assert.Single(Statements(second));
        Assert.Equal("s3:GetObjectAcl", only.GetProperty("Action").GetString());
    }

    [Fact]
    public void MergingTwice_ChangesNothing()
    {
        var once = CrossAccount.MergeBySid(null, new[] { Statement("A"), Statement("B") });

        Assert.Equal(once, CrossAccount.MergeBySid(once, new[] { Statement("A"), Statement("B") }));
    }

    [Fact]
    public void AnExistingStatementWithoutASid_IsPreserved()
    {
        // It cannot be ours — everything written here has a Sid — so it is someone else's.
        const string existing = """{ "Version": "2012-10-17", "Statement": [ { "Effect": "Deny", "Principal": "*", "Action": "s3:*", "Resource": "*" } ] }""";

        var merged = CrossAccount.MergeBySid(existing, new[] { Statement("DevGrant") });

        Assert.Contains(Statements(merged), s => s.GetProperty("Effect").GetString() == "Deny");
    }

    [Fact]
    public void ASingleStatementObject_IsNormalised_NotLost()
    {
        const string existing = """{ "Version": "2012-10-17", "Statement": { "Sid": "Lonely", "Effect": "Allow", "Principal": "*", "Action": "s3:GetObject", "Resource": "*" } }""";

        var merged = CrossAccount.MergeBySid(existing, new[] { Statement("DevGrant") });

        Assert.Equal(2, Statements(merged).Count);
    }

    [Fact]
    public void AnOwnedStatementWithoutASid_IsRefused()
    {
        // It could never be found again to replace, so every run would add another copy.
        var noSid = Statement("x");
        noSid.Remove("Sid");

        Assert.Throws<InvalidOperationException>(() => CrossAccount.MergeBySid(null, new[] { noSid }));
    }

    [Fact]
    public void AnExistingPolicyThatIsNotAnObject_IsRefused_NotOverwritten()
    {
        Assert.Throws<InvalidOperationException>(() => CrossAccount.MergeBySid("[]", new[] { Statement("A") }));
    }

    // ---------------------------------------------------------------------------------------
    //  The build-record read grant
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void TheReadGrant_IsOnlyTheVerifyRole_ReadingImageRecords()
    {
        var grant = CrossAccount.BuildRecordReadGrant(Store, "dev", Dev, "scu-dev-deployer-verify-fn");
        var json = new JsonArray(grant.Select(g => (JsonNode?)g.DeepClone()).ToArray()).ToJsonString();

        var get = grant.Single(s => s["Action"]!.GetValue<string>() == "s3:GetObject");
        Assert.Equal($"arn:aws:s3:::{Store}/image/*", get["Resource"]!.GetValue<string>());

        var list = grant.Single(s => s["Action"]!.GetValue<string>() == "s3:ListBucket");
        Assert.Equal("image/*", list["Condition"]!["StringLike"]!["s3:prefix"]!.GetValue<string>());

        // Narrowed to the one role, by name, in both statements.
        Assert.All(grant, s => Assert.Equal(
            $"arn:aws:iam::{Dev}:role/scu-dev-deployer-verify-fn",
            s["Condition"]!["ArnEquals"]!["aws:PrincipalArn"]!.GetValue<string>()));

        // And nothing that writes, deletes or reaches another class's records.
        foreach (var forbidden in new[] { "PutObject", "Delete", "client/", "s3:*" })
            Assert.DoesNotContain(forbidden, json);
    }

    [Fact]
    public void ThePrincipalIsTheAccount_SoRecreatingTheRoleDoesNotOrphanTheGrant()
    {
        // A role ARN in a resource policy is stored as that role's unique id: delete and recreate the
        // role and the grant points at nothing. The account plus a PrincipalArn condition survives it.
        var grant = CrossAccount.BuildRecordReadGrant(Store, "dev", Dev, "scu-dev-deployer-verify-fn");

        Assert.All(grant, s => Assert.Equal($"arn:aws:iam::{Dev}:root", s["Principal"]!["AWS"]!.GetValue<string>()));
    }

    [Fact]
    public void EachEnvironmentOwnsDifferentSids_SoDevAndProdGrantsCoexist()
    {
        var dev = CrossAccount.BuildRecordReadGrant(Store, "dev", Dev, "scu-dev-deployer-verify-fn");
        var prod = CrossAccount.BuildRecordReadGrant(Store, "prod", Prod, "scu-prod-deployer-verify-fn");

        var merged = CrossAccount.MergeBySid(CrossAccount.MergeBySid(null, dev), prod);

        Assert.Equal(4, Statements(merged).Count);
    }

    // ---------------------------------------------------------------------------------------
    //  The replication permission, in the target registry
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void TheBuildAccountMayReplicate_ButNeverCreateARepository()
    {
        // Without CreateRepository, an image bound for a repository this account has not hardened is
        // refused rather than landing in one replication made — with no immutable tags.
        var statement = CrossAccount.ReplicationPermission(Build, "us-west-2", Dev, Repos);

        Assert.Equal("ecr:ReplicateImage", statement["Action"]!.GetValue<string>());
        Assert.DoesNotContain("CreateRepository", statement.ToJsonString());
        Assert.Equal($"arn:aws:iam::{Build}:root", statement["Principal"]!["AWS"]!.GetValue<string>());
    }

    [Fact]
    public void ThePermissionNamesTheRepositories_NotEveryRepository()
    {
        // repository/* would let the build account replicate into the repositories deploycontainer
        // pushes to today, whose tags are mutable.
        var statement = CrossAccount.ReplicationPermission(Build, "us-west-2", Dev, Repos);

        Assert.Equal(new[] { $"arn:aws:ecr:us-west-2:{Dev}:repository/scu-4df6-b9c6-aiphost" },
            statement["Resource"]!.AsArray().Select(r => r!.GetValue<string>()));
        Assert.DoesNotContain("repository/*", statement.ToJsonString());
    }

    // ---------------------------------------------------------------------------------------
    //  Replication, in the build registry
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void TheRule_ReplicatesExactlyTheNamedRepositories_ToTheTarget()
    {
        var rule = CrossAccount.ReplicationRule("us-west-2", Dev, Repos);

        var destination = Assert.Single(rule.Destinations);
        Assert.Equal((Dev, "us-west-2"), (destination.RegistryId, destination.Region));
        var filter = Assert.Single(rule.RepositoryFilters);
        Assert.Equal("scu-4df6-b9c6-aiphost", filter.Filter);
        Assert.Equal(RepositoryFilterType.PREFIX_MATCH, filter.FilterType);
    }

    private static ReplicationRule Rule(string filter, params (string Account, string Region)[] destinations) => new()
    {
        Destinations = destinations.Select(d => new ReplicationDestination { RegistryId = d.Account, Region = d.Region }).ToList(),
        RepositoryFilters = new List<RepositoryFilter> { new() { Filter = filter, FilterType = RepositoryFilterType.PREFIX_MATCH } },
    };

    [Fact]
    public void AnotherEnvironmentsReplication_IsPreserved()
    {
        var existing = new ReplicationConfiguration { Rules = new List<ReplicationRule> { Rule("prod-only", (Prod, "us-west-2")) } };

        var merged = CrossAccount.MergeReplication(existing, CrossAccount.ReplicationRule("us-west-2", Dev, Repos));

        Assert.Equal(2, merged.Rules.Count);
        Assert.Contains(merged.Rules, r => r.Destinations.Single().RegistryId == Prod && r.RepositoryFilters.Single().Filter == "prod-only");
    }

    [Fact]
    public void ADestinationSharingARuleWithOurs_IsSplitOut_NotDropped()
    {
        // A hand-written rule sending one filter to BOTH accounts. Replacing our destination must not
        // take prod's with it.
        var existing = new ReplicationConfiguration { Rules = new List<ReplicationRule> { Rule("shared", (Dev, "us-west-2"), (Prod, "us-west-2")) } };

        var merged = CrossAccount.MergeReplication(existing, CrossAccount.ReplicationRule("us-west-2", Dev, Repos));

        var prodRule = merged.Rules.Single(r => r.Destinations.Any(d => d.RegistryId == Prod));
        Assert.Equal("shared", prodRule.RepositoryFilters.Single().Filter);
        Assert.DoesNotContain(prodRule.Destinations, d => d.RegistryId == Dev);
        Assert.Single(merged.Rules, r => r.Destinations.Any(d => d.RegistryId == Dev));
    }

    [Fact]
    public void OurOwnEarlierRule_IsReplaced_SoReRunningDoesNotAccumulate()
    {
        var once = CrossAccount.MergeReplication(null, CrossAccount.ReplicationRule("us-west-2", Dev, Repos));
        var twice = CrossAccount.MergeReplication(once, CrossAccount.ReplicationRule("us-west-2", Dev, Repos));

        Assert.Single(twice.Rules);
    }

    [Fact]
    public void ARegistryWithNoReplicationFromTheSdk_IsNotACrash()
    {
        // SDK v4: a collection with no members is null — and a registry never configured is exactly that.
        Assert.Single(CrossAccount.MergeReplication(new ReplicationConfiguration(), CrossAccount.ReplicationRule("us-west-2", Dev, Repos)).Rules);
        Assert.Single(CrossAccount.MergeReplication(null, CrossAccount.ReplicationRule("us-west-2", Dev, Repos)).Rules);
    }

    // ---------------------------------------------------------------------------------------
    //  The account a target-side command runs in
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void TheTargetAccount_MustBeNamed()
    {
        Assert.Throws<InvalidOperationException>(() => CrossAccount.RequireTargetAccount(null, Dev, "bootstrapdeployer"));
    }

    [Fact]
    public void AProfileInAnyOtherAccount_IsRefused_NotOnlyTheBuildAccount()
    {
        // Prod's profile against dev's config would admit dev's replication into prod.
        var ex = Assert.Throws<InvalidOperationException>(() => CrossAccount.RequireTargetAccount(Dev, Prod, "bootstrapdeployer"));
        Assert.Contains(Prod, ex.Message);
    }

    [Fact]
    public void TheNamedAccount_Passes()
    {
        CrossAccount.RequireTargetAccount(Dev, Dev, "bootstrapdeployer");
    }

    // ---------------------------------------------------------------------------------------
    //  Repository hardening, shared by both registries
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void TheLifecyclePolicy_SelectsOnlyUntaggedImages()
    {
        // Tagged images are the deployable identities under immutable tags; a deploy request may name
        // any of them. (Signatures are untagged, and AWS documents that referrers of an image still in
        // use are protected from lifecycle rules until their subject is deleted.)
        using var doc = JsonDocument.Parse(EcrRepositoryHardening.LifecyclePolicy(14));
        var rule = Assert.Single(doc.RootElement.GetProperty("rules").EnumerateArray());
        var selection = rule.GetProperty("selection");

        Assert.Equal("untagged", selection.GetProperty("tagStatus").GetString());
        Assert.Equal(14, selection.GetProperty("countNumber").GetInt32());
        Assert.Equal("expire", rule.GetProperty("action").GetProperty("type").GetString());
    }
}
