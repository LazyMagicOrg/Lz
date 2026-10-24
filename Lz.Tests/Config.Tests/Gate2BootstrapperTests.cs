using Lz.Aws.Pipeline;
using Lz.Core.Config;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// What bootstrapdeployer decides for Gate 2's resources (DecoupledCd.md §15.5, V-E): the hosts from the tenant config, the
/// private subnets by their tags, the cache files to stage, and the project as CodeBuild is asked for it.
/// </summary>
public sealed class Gate2BootstrapperTests
{
    private static (string, TenantConfig) Tenant(string key, string? root, params (string Key, string? SubDomain)[] subtenants) => (key, new TenantConfig
    {
        RootDomain = root,
        Subtenants = subtenants.ToDictionary(s => s.Key, s => new SubtenantEntry { SubDomain = s.SubDomain }),
    });

    // ---- the hosts ----

    [Fact]
    public void TheHosts_AreTheSubtenantsOnTheTenantsRootDomain()
        => Assert.Equal(("match.aiproxydev.click", "aiproxydev.click"), Gate2Bootstrapper.HostsFor(new[] { Tenant("mp", "AiProxyDev.click", ("match", "match")) }));

    [Fact]
    public void ASubtenantWithNoSubDomain_IsHostedUnderItsKey()
        => Assert.Equal("match.aiproxydev.click", Gate2Bootstrapper.HostsFor(new[] { Tenant("mp", "aiproxydev.click", ("match", null)) }).BaseHost);

    [Fact]
    public void NotExactlyOneTenantAndOneSubtenant_IsRefused()
    {
        Assert.Throws<InvalidOperationException>(() => Gate2Bootstrapper.HostsFor(Array.Empty<(string, TenantConfig)>()));
        Assert.Throws<InvalidOperationException>(() => Gate2Bootstrapper.HostsFor(new[] { Tenant("mp", "a.click", ("x", "x"), ("y", "y")) }));
        Assert.Throws<InvalidOperationException>(() => Gate2Bootstrapper.HostsFor(new[] { Tenant("mp", null, ("x", "x")) }));
    }

    // ---- the subnets ----

    // What dev holds (describe-subnets, 2026-10-23): the network component's four, and another VPC's untagged ones.
    private static readonly (string Id, string VpcId, string? Name)[] DevSubnets =
    {
        ("subnet-01e0ec2475ecf0bfc", "vpc-0c9d2cd926f4959a7", "scu-private-subnet-1"),
        ("subnet-0abca4771a5feaf7e", "vpc-0844939c546554dd7", null),
        ("subnet-01a7d4f797ad00ddd", "vpc-0c9d2cd926f4959a7", "scu-public-subnet-1"),
        ("subnet-08e81426ad2fcbdba", "vpc-0c9d2cd926f4959a7", "scu-private-subnet-2"),
    };

    [Fact]
    public void TheBuild_RunsInThePrivateSubnetsAlone()
    {
        var (vpc, subnets) = Gate2Bootstrapper.PrivateSubnetsFor("scu", DevSubnets);
        Assert.Equal("vpc-0c9d2cd926f4959a7", vpc);
        Assert.Equal(new[] { "subnet-01e0ec2475ecf0bfc", "subnet-08e81426ad2fcbdba" }, subnets);
    }

    [Fact]
    public void NoPrivateSubnet_OrPrivateSubnetsInTwoVpcs_IsRefused()
    {
        Assert.Throws<InvalidOperationException>(() => Gate2Bootstrapper.PrivateSubnetsFor("scu", DevSubnets.Where(s => s.Name?.Contains("private") != true).ToList()));
        Assert.Throws<InvalidOperationException>(() => Gate2Bootstrapper.PrivateSubnetsFor("scu",
            DevSubnets.Append(("subnet-x", "vpc-other", "scu-private-subnet-3")).ToList()));
    }

    // ---- the staged cache ----

    [Fact]
    public void WhatIsStaged_IsWhatIsMissingOrChanged_AndNothingIsDeleted()
    {
        var local = new Dictionary<string, string> { ["a.json"] = "aa", ["b.json"] = "bb", ["c.json"] = "cc" };
        var staged = new Dictionary<string, string> { ["a.json"] = "\"aa\"", ["b.json"] = "\"old\"", ["gone.json"] = "\"zz\"" };

        Assert.Equal(new[] { "b.json", "c.json" }, Gate2Bootstrapper.ToStage(local, staged));
    }

    [Fact]
    public void TheLocalCache_IsTheWorkspacesOwn()
        => Assert.Equal(Path.Combine("W", "repos", "Service", "ServiceTest", "E2E", "Fixtures", "cache"), Gate2Bootstrapper.LocalCacheDir("W"));

    // ---- the project ----

    private static DeployerGate2Project Project() => Gate2ProjectPlanner.Plan("scu", "dev", "us-west-2", "503947800380", "scu-dev-gate2-results-4df6-b9c6",
        new DeployerGate2Inputs("match.aiproxydev.click", "aiproxydev.click", "vpc-0c9d2cd926f4959a7", new[] { "subnet-01e0ec2475ecf0bfc", "subnet-08e81426ad2fcbdba" }),
        new PipelineGate1Config { Run = "ServiceTest/bin/Debug/net10.0/ServiceTest.dll", SdkVersion = "10.0.301", SdkSha512 = new string('a', 128) });

    [Fact]
    public void TheProject_IsAskedForInThePrivateSubnets_WithItsSecurityGroup_NoSourceNoArtifacts_Unprivileged()
    {
        var (source, artifacts, environment, vpc, logs) = Gate2Bootstrapper.ProjectSettings(Project(), "sg-123");

        Assert.Equal(Amazon.CodeBuild.SourceType.NO_SOURCE, source.Type);
        Assert.Equal(Project().Buildspec, source.Buildspec);
        Assert.Equal(Amazon.CodeBuild.ArtifactsType.NO_ARTIFACTS, artifacts.Type);
        Assert.False(environment.PrivilegedMode);   // the tiers start no container
        Assert.Equal("vpc-0c9d2cd926f4959a7", vpc.VpcId);
        Assert.Equal(new[] { "sg-123" }, vpc.SecurityGroupIds);
        Assert.Equal(new[] { "subnet-01e0ec2475ecf0bfc", "subnet-08e81426ad2fcbdba" }, vpc.Subnets);
        Assert.Equal("/aws/codebuild/scu-dev-gate2", logs.CloudWatchLogs.GroupName);
        Assert.All(environment.EnvironmentVariables, v => Assert.Equal(Amazon.CodeBuild.EnvironmentVariableType.PLAINTEXT, v.Type));
        Assert.Equal(Project().Environment.Keys.Order(), environment.EnvironmentVariables.Select(v => v.Name));
    }
}
