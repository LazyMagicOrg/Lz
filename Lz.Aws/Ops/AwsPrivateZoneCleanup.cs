using Amazon.Route53;
using Amazon.Route53.Model;
using Amazon.Runtime.CredentialManagement;
using Lz.Aws.Auth;
using Lz.Aws.Compute.Fargate;
using Lz.Aws.Compute.FargateAlb;
using Lz.Aws.Compute.Lambda;
using Lz.Aws.Data;
using Lz.Aws.Edge;
using Lz.Aws.Shared;
using Lz.Aws.Storage;
using Lz.Aws.Tailscale;
using Lz.Aws.Topologies;
using Lz.Aws.Config;
using Lz.Aws.Interfaces;
using Lz.Aws.Interfaces.Outputs;

namespace Lz.Aws.Ops;

/// <summary>
/// Pre-deploy cleanup for Route53 private zones.
/// When the foundation private zone name changes (e.g., from SystemDomain to {systemKey}.internal),
/// Pulumi needs to delete the old zone and create a new one. But Route53 won't delete a zone
/// that contains non-NS/SOA records. These records may have been created by other stacks
/// (e.g., tenant stack creating shop.{domain} in the old private zone).
/// This helper finds the old zone and removes all non-required records so Pulumi can proceed.
/// </summary>
public static class AwsPrivateZoneCleanup
{
    public static async Task CleanupStalePrivateZoneAsync(
        string systemKey, string expectedZoneName, string profile, string region)
    {
        var chain = new CredentialProfileStoreChain();
        if (!chain.TryGetAWSCredentials(profile, out var credentials))
            return;

        using var client = new AmazonRoute53Client(credentials, Amazon.RegionEndpoint.GetBySystemName(region));

        // Find private zones tagged with this system
        var zones = await client.ListHostedZonesAsync();
        foreach (var zone in zones.HostedZones)
        {
            if (zone.Config.PrivateZone != true) continue;

            // Skip the expected zone name — that's the one we want to keep/create
            var zoneName = zone.Name.TrimEnd('.');
            if (zoneName.Equals(expectedZoneName, StringComparison.OrdinalIgnoreCase))
                continue;

            // Check if this zone belongs to our system by looking at the comment
            if (zone.Config.Comment == null ||
                !zone.Config.Comment.Contains(systemKey, StringComparison.OrdinalIgnoreCase))
                continue;

            // This is a stale private zone — clear its non-required records
            Console.WriteLine($"  Cleaning up stale private zone: {zoneName} ({zone.Id})");
            await DeleteNonRequiredRecordsAsync(client, zone.Id);
        }
    }

    private static async Task DeleteNonRequiredRecordsAsync(
        AmazonRoute53Client client, string hostedZoneId)
    {
        var zoneId = hostedZoneId.Replace("/hostedzone/", "");
        var records = await client.ListResourceRecordSetsAsync(
            new ListResourceRecordSetsRequest { HostedZoneId = zoneId });

        var changes = new List<Change>();
        foreach (var rrs in records.ResourceRecordSets)
        {
            // NS and SOA are required and auto-deleted when the zone is removed
            if (rrs.Type == RRType.NS || rrs.Type == RRType.SOA)
                continue;

            changes.Add(new Change
            {
                Action = ChangeAction.DELETE,
                ResourceRecordSet = rrs,
            });
        }

        if (changes.Count == 0)
            return;

        Console.WriteLine($"    Deleting {changes.Count} stale record(s)...");
        await client.ChangeResourceRecordSetsAsync(new ChangeResourceRecordSetsRequest
        {
            HostedZoneId = zoneId,
            ChangeBatch = new ChangeBatch { Changes = changes },
        });
        Console.WriteLine($"    Done.");
    }
}
