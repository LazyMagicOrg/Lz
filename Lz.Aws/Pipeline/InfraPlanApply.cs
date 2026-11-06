using Amazon.EC2;
using Amazon.ECS;
using Amazon.IdentityManagement;
using Ec2 = Amazon.EC2.Model;

namespace Lz.Aws.Pipeline;

/// <summary>
/// <c>lz bootstrapdeployer --apply</c>'s half of the infrastructure branch, plan only (DecoupledCd.md P3 D): the Plan task's
/// network (P-13), its cluster, its log group, and its two roles. Every piece is found again before it is made, so a second
/// apply changes nothing; nothing is ever deleted here. Returns the subnet and group the functions are configured with.
/// </summary>
public static class InfraPlanApply
{
    /// <summary>The network as it exists, found by its tag; null when it is not all there yet (read in the dry run too).</summary>
    public static async Task<DeployerInfraInputs?> FindAsync(IAmazonEC2 ec2, InfraPlanNetwork network)
    {
        var subnets = (await ec2.DescribeSubnetsAsync(new Ec2.DescribeSubnetsRequest { Filters = Tagged(network) })).Subnets ?? new();
        var groups = (await ec2.DescribeSecurityGroupsAsync(new Ec2.DescribeSecurityGroupsRequest { Filters = Tagged(network) })).SecurityGroups ?? new();
        if (subnets.Count > 1 || groups.Count > 1)
            throw new InvalidOperationException(
                $"{subnets.Count} subnets and {groups.Count} security groups carry {InfraPlanNetwork.Tag}={network.Name}; the Plan task runs in " +
                "exactly one of each. Remove the extra by hand: this command deletes nothing.");
        return subnets.Count == 1 && groups.Count == 1 ? new DeployerInfraInputs(subnets[0].SubnetId, groups[0].GroupId) : null;
    }

    public static async Task<DeployerInfraInputs> EnsureAsync(
        IAmazonEC2 ec2, IAmazonECS ecs, IAmazonIdentityManagementService iam, Amazon.CloudWatchLogs.IAmazonCloudWatchLogs logs,
        InfraPlanResources r, string denyPolicy)
    {
        var inputs = await EnsureNetworkAsync(ec2, r.Network);

        // The cluster: CreateCluster answers with the existing one when it is ACTIVE.
        await ecs.CreateClusterAsync(new Amazon.ECS.Model.CreateClusterRequest
        {
            ClusterName = r.ClusterName,
            Tags = new List<Amazon.ECS.Model.Tag> { new() { Key = InfraPlanNetwork.Tag, Value = r.Network.Name } },
        });
        Console.WriteLine($"  cluster {r.ClusterName}");

        await DeployerBootstrapper.EnsureLogGroupAsync(logs, r.LogGroup, r.LogRetentionDays);

        await DeployerBootstrapper.EnsureRoleAsync(iam, r.ExecutionRoleName, r.TaskTrustPolicy,
            "lz decoupled-CD deployer: pulls the tooling image and writes the Plan task's log.",
            ($"{r.ExecutionRoleName}-pull", r.ExecutionRolePolicy), denyPolicy);
        await DeployerBootstrapper.EnsureRoleAsync(iam, r.TaskRoleName, r.TaskTrustPolicy,
            "lz decoupled-CD deployer: the Plan task. Reads the account, writes its plan under plans/.",
            ($"{r.TaskRoleName}-plan", r.TaskRolePolicy), denyPolicy);
        foreach (var managed in r.TaskRoleManagedPolicies)
            await iam.AttachRolePolicyAsync(new Amazon.IdentityManagement.Model.AttachRolePolicyRequest { RoleName = r.TaskRoleName, PolicyArn = managed });
        Console.WriteLine($"  roles {r.ExecutionRoleName}, {r.TaskRoleName} (+ {string.Join(", ", r.TaskRoleManagedPolicies)})");

        return inputs;
    }

    private static async Task<DeployerInfraInputs> EnsureNetworkAsync(IAmazonEC2 ec2, InfraPlanNetwork n)
    {
        var vpcs = (await ec2.DescribeVpcsAsync(new Ec2.DescribeVpcsRequest { Filters = Tagged(n) })).Vpcs ?? new();
        var vpcId = vpcs.Count switch
        {
            0 => (await ec2.CreateVpcAsync(new Ec2.CreateVpcRequest { CidrBlock = n.VpcCidr, TagSpecifications = Tags(Amazon.EC2.ResourceType.Vpc, n) })).Vpc.VpcId,
            1 => vpcs[0].VpcId,
            _ => throw new InvalidOperationException($"{vpcs.Count} VPCs carry {InfraPlanNetwork.Tag}={n.Name}; remove the extra by hand."),
        };

        var gateways = (await ec2.DescribeInternetGatewaysAsync(new Ec2.DescribeInternetGatewaysRequest { Filters = Tagged(n) })).InternetGateways ?? new();
        var gateway = gateways.FirstOrDefault()
            ?? (await ec2.CreateInternetGatewayAsync(new Ec2.CreateInternetGatewayRequest { TagSpecifications = Tags(Amazon.EC2.ResourceType.InternetGateway, n) })).InternetGateway;
        if (!(gateway.Attachments ?? new()).Any(a => a.VpcId == vpcId))
            await ec2.AttachInternetGatewayAsync(new Ec2.AttachInternetGatewayRequest { InternetGatewayId = gateway.InternetGatewayId, VpcId = vpcId });

        var subnets = (await ec2.DescribeSubnetsAsync(new Ec2.DescribeSubnetsRequest { Filters = Tagged(n) })).Subnets ?? new();
        var subnetId = subnets.FirstOrDefault()?.SubnetId
            ?? (await ec2.CreateSubnetAsync(new Ec2.CreateSubnetRequest
            {
                VpcId = vpcId, CidrBlock = n.SubnetCidr, AvailabilityZone = n.AvailabilityZone, TagSpecifications = Tags(Amazon.EC2.ResourceType.Subnet, n),
            })).Subnet.SubnetId;

        var tables = (await ec2.DescribeRouteTablesAsync(new Ec2.DescribeRouteTablesRequest { Filters = Tagged(n) })).RouteTables ?? new();
        var table = tables.FirstOrDefault()
            ?? (await ec2.CreateRouteTableAsync(new Ec2.CreateRouteTableRequest { VpcId = vpcId, TagSpecifications = Tags(Amazon.EC2.ResourceType.RouteTable, n) })).RouteTable;
        if (!(table.Routes ?? new()).Any(rt => rt.DestinationCidrBlock == "0.0.0.0/0" && rt.GatewayId == gateway.InternetGatewayId))
            await ec2.CreateRouteAsync(new Ec2.CreateRouteRequest
            {
                RouteTableId = table.RouteTableId, DestinationCidrBlock = "0.0.0.0/0", GatewayId = gateway.InternetGatewayId,
            });
        if (!(table.Associations ?? new()).Any(a => a.SubnetId == subnetId))
            await ec2.AssociateRouteTableAsync(new Ec2.AssociateRouteTableRequest { RouteTableId = table.RouteTableId, SubnetId = subnetId });

        // NO INBOUND RULE: a new group has none, and none is ever added. Outbound is the default, everywhere: the task reaches
        // AWS's public endpoints and the registry through the gateway.
        var groups = (await ec2.DescribeSecurityGroupsAsync(new Ec2.DescribeSecurityGroupsRequest { Filters = Tagged(n) })).SecurityGroups ?? new();
        var groupId = groups.FirstOrDefault()?.GroupId
            ?? (await ec2.CreateSecurityGroupAsync(new Ec2.CreateSecurityGroupRequest
            {
                GroupName = n.Name, Description = "lz deployer Plan task: no inbound rule", VpcId = vpcId,
                TagSpecifications = Tags(Amazon.EC2.ResourceType.SecurityGroup, n),
            })).GroupId;
        if (groups.FirstOrDefault() is { } existing && (existing.IpPermissions ?? new()).Count > 0)
            throw new InvalidOperationException(
                $"security group {existing.GroupId} ({n.Name}) has {existing.IpPermissions!.Count} inbound rule(s); the Plan task's has none. " +
                "Someone added one by hand: remove it, then apply again.");

        Console.WriteLine($"  network {n.Name}: {vpcId} {n.VpcCidr}, subnet {subnetId} {n.SubnetCidr} in {n.AvailabilityZone}, " +
                          $"gateway {gateway.InternetGatewayId}, route table {table.RouteTableId}, group {groupId} (no inbound rule)");
        return new DeployerInfraInputs(subnetId, groupId);
    }

    private static List<Ec2.Filter> Tagged(InfraPlanNetwork n)
        => new() { new Ec2.Filter { Name = $"tag:{InfraPlanNetwork.Tag}", Values = new List<string> { n.Name } } };

    private static List<Ec2.TagSpecification> Tags(Amazon.EC2.ResourceType type, InfraPlanNetwork n) => new()
    {
        new Ec2.TagSpecification
        {
            ResourceType = type,
            Tags = new List<Ec2.Tag> { new() { Key = InfraPlanNetwork.Tag, Value = n.Name }, new() { Key = "Name", Value = n.Name } },
        },
    };
}
