using Lz.Core.Config;
using Lz.Core.Interfaces.Outputs;
using Pulumi;
using Pulumi.Aws.Acm;
using Pulumi.Aws.LB;
using Pulumi.Aws.Route53;
using Pulumi.Aws.Route53.Inputs;
using AcmCertificate = Pulumi.Aws.Acm.Certificate;
using AcmCertificateArgs = Pulumi.Aws.Acm.CertificateArgs;
using Route53Record = Pulumi.Aws.Route53.Record;
using Route53RecordArgs = Pulumi.Aws.Route53.RecordArgs;
using Lz.Aws.Auth;
using Lz.Aws.Compute.Fargate;
using Lz.Aws.Compute.Lambda;
using Lz.Aws.Data;
using Lz.Aws.Edge;
using Lz.Aws.Ops;
using Lz.Aws.Shared;
using Lz.Aws.Storage;
using Lz.Aws.Tailscale;
using Lz.Aws.Topologies;
using Lz.Aws.Config;
using Lz.Aws.Interfaces;
using Lz.Aws.Interfaces.Outputs;

namespace Lz.Aws.Compute.FargateAlb;

/// <summary>
/// Per-tenant ALB certificate (SNI) and DNS records.
/// Every tenant creates:
///   - ACM cert: *.{RootDomain}, *.shop.{RootDomain} + LegacyDomains SANs
///   - ListenerCertificate: attaches to public + internal ALB listeners (SNI)
///   - DNS: origin.{RootDomain} → ALB (A alias)
///   - DNS for each LegacyDomain: same records
///   - All records use AllowOverwrite = true
/// </summary>
public class AwsTenantDnsAndCertComponent : ComponentResource
{
    public AwsTenantDnsAndCertComponent()
        // FROZEN Pulumi type token: deployed-state URN identity — deliberately NOT
        // renamed in the 0.11.0 axis restructure (renaming would replace deployed
        // resources). See Lz/Migrations/AxisRestructure.md.
        : base("lz:aws:TenantDnsAndCert", "tenant-dns-cert", ResourceArgs.Empty, null)
    {
    }

    public void Deploy(
        TenantConfig tenantConfig,
        INetworkOutputs network,
        ICdnOutputs? cdn = null)
    {
        var sk = tenantConfig.SystemKey;
        var tk = tenantConfig.TenantKey;
        var prefix = $"{sk}-{tk}";
        var domain = tenantConfig.RootDomain;
        var awsNetwork = (AwsFargateAlbNetworkOutputs)network;
        var opts = new CustomResourceOptions { Parent = this };

        // Collect all domains: root + legacy
        var allDomains = new List<string> { domain };
        if (tenantConfig.LegacyDomains != null)
            allDomains.AddRange(tenantConfig.LegacyDomains);

        Pulumi.Log.Info($"TenantDnsAndCert: allDomains = [{string.Join(", ", allDomains)}], cdn = {(cdn != null ? "provided" : "null")}");

        // Look up PUBLIC Route53 hosted zones for all domains using AWS SDK directly.
        // We cannot use Pulumi's GetZone data source here because this component also
        // creates private zones with the same domain names. Pulumi's GetZone may resolve
        // to the private zone even with PrivateZone=false due to execution ordering.
        var zonesByDomain = new Dictionary<string, Output<string>>();
        var profile = tenantConfig.Profile ?? "";
        var region = tenantConfig.Region ?? "us-west-2";
        foreach (var d in allDomains)
        {
            var publicZoneId = LookupPublicZoneId(d, profile, region);
            zonesByDomain[d] = Output.Create(publicZoneId);
        }

        // =====================================================================
        // ACM CERTIFICATE for ALB (same region as ALB)
        // =====================================================================

        // Build SANs: wildcard + shop wildcard for each domain
        var sans = new InputList<string>();
        sans.Add($"*.{domain}");
        sans.Add($"*.shop.{domain}");
        foreach (var d in allDomains)
        {
            if (d != domain)
            {
                sans.Add(d);
                sans.Add($"*.{d}");
                sans.Add($"*.shop.{d}");
            }
        }

        var cert = new AcmCertificate($"{prefix}-alb-cert", new AcmCertificateArgs
        {
            DomainName = domain,
            SubjectAlternativeNames = sans,
            ValidationMethod = "DNS",
            Tags = Tags(sk, tk, "alb-cert"),
        }, opts);

        // DNS validation records — one per unique domain stem.
        // ACM generates one validation CNAME per base domain:
        //   monrotest.click + *.monrotest.click share one CNAME
        //   *.shop.monrotest.click gets a separate CNAME (different stem)
        //   Each legacy domain gets its own CNAME
        // We create validation records for each unique stem, matched by domain name.
        var validationFqdns = new InputList<string>();

        // Build list of all domain stems that need validation records.
        // Each entry: (slug for Pulumi name, domain to match in DomainValidationOptions, zone to write to)
        var validationTargets = new List<(string slug, string matchDomain, Output<string> zoneId)>();
        foreach (var d in allDomains)
        {
            var safeName = d.Replace(".", "-");
            // Base domain: matches DomainValidationOptions where DomainName == d or *.{d}
            validationTargets.Add((safeName, d, zonesByDomain[d]));
            // Shop subdomain: matches DomainValidationOptions where DomainName == *.shop.{d}
            validationTargets.Add(($"shop-{safeName}", $"shop.{d}", zonesByDomain[d]));
        }

        foreach (var (slug, matchDomain, zoneId) in validationTargets)
        {
            var valRecord = new Route53Record($"{prefix}-cert-val-{slug}", new Route53RecordArgs
            {
                ZoneId = zoneId,
                Name = cert.DomainValidationOptions.Apply(opts =>
                    opts.First(o => o.DomainName == matchDomain
                        || o.DomainName == $"*.{matchDomain}"
                        || o.DomainName == matchDomain.Replace("*.", ""))
                    .ResourceRecordName!),
                Type = cert.DomainValidationOptions.Apply(opts =>
                    opts.First(o => o.DomainName == matchDomain
                        || o.DomainName == $"*.{matchDomain}"
                        || o.DomainName == matchDomain.Replace("*.", ""))
                    .ResourceRecordType!),
                Records = { cert.DomainValidationOptions.Apply(opts =>
                    opts.First(o => o.DomainName == matchDomain
                        || o.DomainName == $"*.{matchDomain}"
                        || o.DomainName == matchDomain.Replace("*.", ""))
                    .ResourceRecordValue!) },
                Ttl = 300,
                AllowOverwrite = true,
            }, opts);
            validationFqdns.Add(valRecord.Fqdn);
        }

        var certValidation = new CertificateValidation($"{prefix}-cert-validated", new CertificateValidationArgs
        {
            CertificateArn = cert.Arn,
            ValidationRecordFqdns = validationFqdns,
        }, opts);

        // =====================================================================
        // LISTENER CERTIFICATES (SNI — attach to existing ALB listeners)
        // =====================================================================

        new ListenerCertificate($"{prefix}-public-sni", new ListenerCertificateArgs
        {
            ListenerArn = awsNetwork.HttpsListenerArn,
            CertificateArn = certValidation.CertificateArn,
        }, opts);

        new ListenerCertificate($"{prefix}-internal-sni", new ListenerCertificateArgs
        {
            ListenerArn = awsNetwork.InternalHttpsListenerArn,
            CertificateArn = certValidation.CertificateArn,
        }, opts);

        // =====================================================================
        // PRIVATE DNS ZONE — VPN access to tenant services
        // =====================================================================
        // Each tenant gets a VPC-associated private zone for its RootDomain.
        // This allows VPN users to resolve shop.{RootDomain} → internal ALB.
        // Without this, VPN users can't reach tenant-specific SmartStore instances.

        foreach (var d in allDomains)
        {
            var safeName = d.Replace(".", "-");

            var tenantPrivateZone = new Zone($"{prefix}-private-{safeName}", new ZoneArgs
            {
                Name = d,
                Vpcs =
                {
                    new ZoneVpcArgs { VpcId = awsNetwork.NetworkId },
                },
                Comment = $"Private zone for tenant {tk} - VPN access to {d}",
                Tags = Tags(sk, tk, $"private-zone-{safeName}"),
            }, opts);

            // shop.{domain} → internal ALB (SmartStore VPN access)
            new Route53Record($"{prefix}-private-shop-{safeName}", new Route53RecordArgs
            {
                ZoneId = tenantPrivateZone.ZoneId,
                Name = $"shop.{d}",
                Type = "A",
                Aliases =
                {
                    new RecordAliasArgs
                    {
                        Name = awsNetwork.InternalAlbDns,
                        ZoneId = awsNetwork.InternalAlbZoneId,
                        EvaluateTargetHealth = true,
                    },
                },
            }, opts);

            // *.shop.{domain} → internal ALB (subtenant/regional stores, e.g., ca.shop.{domain})
            new Route53Record($"{prefix}-private-shop-wildcard-{safeName}", new Route53RecordArgs
            {
                ZoneId = tenantPrivateZone.ZoneId,
                Name = $"*.shop.{d}",
                Type = "A",
                Aliases =
                {
                    new RecordAliasArgs
                    {
                        Name = awsNetwork.InternalAlbDns,
                        ZoneId = awsNetwork.InternalAlbZoneId,
                        EvaluateTargetHealth = true,
                    },
                },
            }, opts);

            // auth.{domain} → internal ALB (Keycloak admin VPN access via tenant domain)
            new Route53Record($"{prefix}-private-auth-{safeName}", new Route53RecordArgs
            {
                ZoneId = tenantPrivateZone.ZoneId,
                Name = $"auth.{d}",
                Type = "A",
                Aliases =
                {
                    new RecordAliasArgs
                    {
                        Name = awsNetwork.InternalAlbDns,
                        ZoneId = awsNetwork.InternalAlbZoneId,
                        EvaluateTargetHealth = true,
                    },
                },
            }, opts);
        }

        // =====================================================================
        // PUBLIC DNS RECORDS
        // =====================================================================
        // All DNS records for all domains (root + legacy) are managed here
        // with stable resource names keyed by domain slug. This prevents
        // resource identity conflicts when domains switch roles during transitions.
        //
        // CloudFront hosted zone ID is a global constant for all distributions.
        var cfHostedZoneId = "Z2FDTNDATAQYW2";

        foreach (var d in allDomains)
        {
            var safeName = d.Replace(".", "-");

            // Use ReplaceOnChanges for zone ID so records move correctly between zones.
            var dnsOpts = new CustomResourceOptions
            {
                Parent = this,
                DeleteBeforeReplace = true,
            };

            // origin.{domain} → public ALB (CloudFront origin endpoint)
            new Route53Record($"{prefix}-pub-origin-{safeName}", new Route53RecordArgs
            {
                ZoneId = zonesByDomain[d],
                Name = $"origin.{d}",
                Type = "A",
                AllowOverwrite = true,
                Aliases =
                {
                    new RecordAliasArgs
                    {
                        Name = awsNetwork.PublicAlbDns,
                        ZoneId = awsNetwork.PublicAlbZoneId,
                        EvaluateTargetHealth = true,
                    },
                },
            }, dnsOpts);

            // {domain} apex → CloudFront
            if (cdn != null)
            {
                new Route53Record($"{prefix}-pub-apex-{safeName}", new Route53RecordArgs
                {
                    ZoneId = zonesByDomain[d],
                    Name = d,
                    Type = "A",
                    AllowOverwrite = true,
                    Aliases =
                    {
                        new RecordAliasArgs
                        {
                            Name = cdn.DomainName,
                            ZoneId = cfHostedZoneId,
                            EvaluateTargetHealth = false,
                        },
                    },
                }, dnsOpts);

                // *.{domain} → CloudFront
                new Route53Record($"{prefix}-pub-wildcard-{safeName}", new Route53RecordArgs
                {
                    ZoneId = zonesByDomain[d],
                    Name = $"*.{d}",
                    Type = "A",
                    AllowOverwrite = true,
                    Aliases =
                    {
                        new RecordAliasArgs
                        {
                            Name = cdn.DomainName,
                            ZoneId = cfHostedZoneId,
                            EvaluateTargetHealth = false,
                        },
                    },
                }, dnsOpts);
            }
        }
    }

    /// <summary>
    /// Look up the public Route53 hosted zone ID for a domain using the AWS SDK directly.
    /// This avoids Pulumi's GetZone data source which can return private zones
    /// when both public and private zones exist for the same domain.
    /// </summary>
    private static string LookupPublicZoneId(string domainName, string profile, string region)
    {
        var chain = new Amazon.Runtime.CredentialManagement.CredentialProfileStoreChain();
        chain.TryGetAWSCredentials(profile, out var credentials);
        using var client = credentials != null
            ? new Amazon.Route53.AmazonRoute53Client(credentials, Amazon.RegionEndpoint.GetBySystemName(region))
            : new Amazon.Route53.AmazonRoute53Client(Amazon.RegionEndpoint.GetBySystemName(region));
        var response = client.ListHostedZonesByNameAsync(
            new Amazon.Route53.Model.ListHostedZonesByNameRequest
            {
                DNSName = domainName,
                MaxItems = "10",
            }).GetAwaiter().GetResult();

        var zone = response.HostedZones
            .FirstOrDefault(z =>
                z.Name.TrimEnd('.').Equals(domainName, StringComparison.OrdinalIgnoreCase)
                && z.Config.PrivateZone != true);

        if (zone == null)
            throw new InvalidOperationException(
                $"No public Route53 hosted zone found for '{domainName}'. " +
                $"Create the zone before running deploytenant.");

        return zone.Id.Replace("/hostedzone/", "");
    }

    private static InputMap<string> Tags(string sk, string tk, string resource) => new()
    {
        { "System", sk },
        { "Tenant", tk },
        { "Resource", resource },
        { "ManagedBy", "lz-pulumi" },
    };
}
