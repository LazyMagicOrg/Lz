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

namespace Lz.Aws.Ecs;

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
        : base("lz:aws:TenantDnsAndCert", "tenant-dns-cert", ResourceArgs.Empty, null)
    {
    }

    public void Deploy(
        TenantConfig tenantConfig,
        INetworkOutputs network)
    {
        var sk = tenantConfig.SystemKey;
        var tk = tenantConfig.TenantKey;
        var prefix = $"{sk}-{tk}";
        var domain = tenantConfig.RootDomain;
        var awsNetwork = (AwsNetworkOutputs)network;
        var opts = new CustomResourceOptions { Parent = this };

        // Collect all domains: root + legacy
        var allDomains = new List<string> { domain };
        if (tenantConfig.LegacyDomains != null)
            allDomains.AddRange(tenantConfig.LegacyDomains);

        // Look up Route53 hosted zones for all domains
        var zonesByDomain = new Dictionary<string, Output<string>>();
        foreach (var d in allDomains)
        {
            var zone = GetZone.Invoke(
                new GetZoneInvokeArgs { Name = d });
            zonesByDomain[d] = zone.Apply(z => z.ZoneId);
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
        // PUBLIC DNS RECORDS — origin.{domain} + *.{domain} → ALB
        // =====================================================================

        foreach (var d in allDomains)
        {
            var safeName = d.Replace(".", "-");

            // origin.{domain} → public ALB (used by CloudFront as origin)
            new Route53Record($"{prefix}-origin-{safeName}", new Route53RecordArgs
            {
                ZoneId = zonesByDomain[d],
                Name = $"origin.{d}",
                Type = "A",
                Aliases =
                {
                    new RecordAliasArgs
                    {
                        Name = awsNetwork.PublicAlbDns,
                        ZoneId = awsNetwork.PublicAlbZoneId,
                        EvaluateTargetHealth = true,
                    },
                },
                AllowOverwrite = true,
            }, opts);

            // *.{domain} is NOT created here — CloudFront owns the public wildcard
            // record (*.{domain} → CloudFront). Only origin.{domain} → ALB is needed
            // as the CloudFront origin endpoint.
        }
    }

    private static InputMap<string> Tags(string sk, string tk, string resource) => new()
    {
        { "System", sk },
        { "Tenant", tk },
        { "Resource", resource },
        { "ManagedBy", "lz-pulumi" },
    };
}
