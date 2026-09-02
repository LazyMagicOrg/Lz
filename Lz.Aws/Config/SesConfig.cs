namespace Lz.Aws.Config;

/// <summary>
/// OPT-IN cross-account SES sending. Absent (null) ⇒ NOTHING is emitted and the plan is
/// byte-identical to a deploy without it — the same opt-in-null contract as
/// <see cref="PrivateNetworkConfig"/> and VectorStore. This matters because
/// <see cref="AwsSystemConfig"/> is shared by every system that vendors this framework.
///
/// <para><b>Why a role ARN rather than SES settings.</b> The SES identity deliberately lives in a
/// DIFFERENT account from the workload. SES <i>sending authorization</i> (an identity policy on the
/// owning account's domain) does not help: AWS charges the DELEGATE's quota and sandbox status —
/// "bounces and complaints count toward the bounce and complaint metrics for your AWS account, and
/// the number of messages you send counts toward your sending quota", and in the sandbox "neither
/// you nor the delegate sender can send email to non-verified email addresses". So the workload
/// ASSUMES a role in the identity account and sends as that account, which is what makes one
/// production-access request cover every environment.</para>
/// </summary>
public class SesConfig
{
    /// <summary>
    /// Role to assume in the SES identity account, e.g.
    /// <c>arn:aws:iam::982408502448:role/scu-ses-sender-dev</c>. The task/execution role is granted
    /// <c>sts:AssumeRole</c> on exactly this ARN — never a wildcard.
    ///
    /// <para>This grant is the identity-side half of cross-account access. The trust policy on the
    /// target role is necessary but NOT sufficient; both keys are required.</para>
    /// </summary>
    public string? SenderRoleArn { get; set; }

    /// <summary>Envelope/From address, e.g. <c>notify@notify-dev.scutara.com</c>. Surfaced as
    /// <c>Ses:FromAddress</c>. Must be within a domain the target role is scoped to send as.</summary>
    public string? FromAddress { get; set; }

    /// <summary>Region the SES identity lives in. Surfaced as <c>Ses:Region</c>. SES sandbox status
    /// and production access are per-account AND per-region, so this is not always the system's own
    /// region and must be stated rather than inferred.</summary>
    public string? Region { get; set; }
}
