using Lz.Core.Config;
using Lz.Aws.Config;
using Lz.Core.Interfaces;
using Lz.Core.Interfaces.Outputs;
using Pulumi;
using Pulumi.Aws.Efs;
using Pulumi.Aws.Efs.Inputs;

namespace Lz.Aws.Ecs;

/// <summary>
/// AWS EFS component — encrypted filesystem, mount targets, and access points.
/// </summary>
public class AwsEfsComponent : ComponentResource, IFileStorageComponent
{
    public AwsEfsComponent()
        : base("lz:aws:Efs", "filestorage", ResourceArgs.Empty, null)
    {
    }

    public IFileStorageOutputs Deploy(SystemConfig config, INetworkOutputs network)
    {
        var prefix = config.SystemKey;
        var opts = new CustomResourceOptions { Parent = this };
        var awsNetwork = (AwsNetworkOutputs)network;

        // EFS File System
        var fs = new FileSystem($"{prefix}-efs", new FileSystemArgs
        {
            Encrypted = true,
            PerformanceMode = "generalPurpose",
            ThroughputMode = "bursting",
            Tags =
            {
                { "Name", $"{prefix}-efs" },
                { "System", config.SystemKey },
                { "Environment", config.Environment },
                { "ManagedBy", "lz-pulumi" },
            },
        }, new CustomResourceOptions { Parent = this, Protect = config.Environment is "prod" or "staging" });

        // Mount Targets (one per private subnet / AZ)
        network.PrivateSubnetIds.Apply(subnetIds =>
        {
            for (int i = 0; i < subnetIds.Length; i++)
            {
                new MountTarget($"{prefix}-efs-mt-{i + 1}", new MountTargetArgs
                {
                    FileSystemId = fs.Id,
                    SubnetId = subnetIds[i],
                    SecurityGroups = { awsNetwork.EfsSecurityGroupId },
                }, opts);
            }
            return subnetIds;
        });

        // Keycloak Theme Access Point
        var ecs = config.Aws().ECS ?? new EcsConfig();
        var themePath = ecs.KeycloakThemePath;
        var themeLabel = themePath.TrimStart('/').Replace("/", "-");
        var keycloakThemeAp = new AccessPoint($"{prefix}-keycloak-theme-ap", new AccessPointArgs
        {
            FileSystemId = fs.Id,
            PosixUser = new AccessPointPosixUserArgs
            {
                Uid = 1000,
                Gid = 1000,
            },
            RootDirectory = new AccessPointRootDirectoryArgs
            {
                Path = themePath,
                CreationInfo = new AccessPointRootDirectoryCreationInfoArgs
                {
                    OwnerUid = 1000,
                    OwnerGid = 1000,
                    Permissions = "755",
                },
            },
            Tags =
            {
                { "Name", $"{prefix}-{themeLabel}" },
                { "System", config.SystemKey },
                { "ManagedBy", "lz-pulumi" },
            },
        }, opts);

        return new Lz.Aws.Shared.AwsFileStorageOutputs
        {
            FileSystemId = fs.Id,
            FileSystemArn = fs.Arn,
            KeycloakThemeAccessPointId = keycloakThemeAp.Id,
        };
    }
}
