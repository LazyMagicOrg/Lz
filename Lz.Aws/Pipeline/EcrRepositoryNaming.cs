using Lz.Core.Config;

namespace Lz.Aws.Pipeline;

/// <summary>
/// What an ECR repository is called, under each of the two naming modes.
///
/// <para>TODAY: <c>{sk}-{suffix}-{env}-{tk}-{svc}</c> — <c>scu-4df6-b9c6-dev-mp-aiphost</c>. That
/// name carries an environment AND a tenant, which made sense when a person ran
/// <c>lz deploycontainer</c> against one environment at a time.</para>
///
/// <para>NEUTRAL: <c>{sk}-{suffix}-{svc}</c> — <c>scu-4df6-b9c6-aiphost</c>. Both segments go, and
/// the tenant matters as much as the environment:</para>
///
/// <list type="bullet">
///   <item><b>Environment</b>, because an image is built once and PROMOTED by replication. A name
///     containing <c>-dev-</c> cannot be the same artifact in prod, and "the thing that arrives in
///     prod is provably the thing that was built" is the property the whole design rests on.</item>
///   <item><b>Tenant</b>, because the image does not have one. Tenancy reaches the container at
///     RUNTIME through the <c>lz-config</c> / <c>lz-tenantid</c> headers, so every tenant's
///     repository would hold identical bytes. Decisively, the pipeline's build side has no tenant
///     concept at all: <c>Pipeline.Repositories</c> names repositories and classes, never tenants,
///     so a build workflow would have to enumerate tenants to push the same image twice — doubling
///     storage, scanning and signing for nothing.</item>
/// </list>
///
/// <para>ECR REPOSITORIES CANNOT BE RENAMED IN PLACE, so switching modes on a live system creates
/// the new repository and leaves the old one holding history. That is a migration, not a config
/// edit; nothing here deletes anything.</para>
/// </summary>
public static class EcrRepositoryNaming
{
    /// <summary>
    /// The repository name for one service under this system's configured mode.
    /// </summary>
    /// <param name="tenantKey">
    /// Required for the environment mode, ignored by the neutral one. Null is accepted and means
    /// "neutral mode only" — passing it for the environment mode is a caller error, not a default.
    /// </param>
    public static string For(SystemConfig config, string serviceName, string? tenantKey = null)
        => IsNeutral(config)
            ? Neutral(config.SystemKey, config.SystemSuffix, serviceName)
            : Environment(config.SystemKey, config.SystemSuffix, config.Environment,
                          tenantKey ?? throw new InvalidOperationException(
                              $"ECR repository name for '{serviceName}' needs a tenant key under the " +
                              "'environment' naming mode. Pass one, or set " +
                              "Pipeline.Registry.RepositoryNaming: neutral."),
                          serviceName);

    /// <summary><c>{sk}-{suffix}-{svc}</c> — no environment, no tenant.</summary>
    public static string Neutral(string systemKey, string suffix, string serviceName)
        => $"{systemKey}-{suffix}-{serviceName}";

    /// <summary><c>{sk}-{suffix}-{env}-{tk}-{svc}</c> — what every repository is called today.</summary>
    public static string Environment(
        string systemKey, string suffix, string environment, string tenantKey, string serviceName)
        => $"{systemKey}-{suffix}-{environment}-{tenantKey}-{serviceName}";

    /// <summary>
    /// The pipeline's repository for one tenant service, in this environment's registry — or null, which
    /// every caller treats as "there is no pipeline here" and answers exactly as it did before the pipeline
    /// existed.
    ///
    /// <para><b>THE DEPLOY PATH'S ONLY READ OF THE BLOCK.</b> <c>lz deploytenant</c> (through
    /// <c>SystemDeployment.ResolveImageDigestsAsync</c>) and <c>lz updatecontainer</c> call this and nothing
    /// else, so what they can learn from <c>Pipeline</c> is exactly this one value. Null unless ALL of: the
    /// block is present and enabled; this environment accepts the <c>image</c> class; a repository entry of
    /// class <c>image</c> names this service among its <c>Artifacts</c>; and <c>TargetAccountId</c> says
    /// whose registry the image is replicated into — without it no pipeline image can be told from any
    /// other, and <c>lz bootstrapdeployer</c> refuses to build one.</para>
    /// </summary>
    public static Lz.Aws.Compute.PipelineImageSource? PipelineImageSourceFor(
        SystemConfig config, string serviceName, string tenantKey, string region)
    {
        if (config.Pipeline is not { Enabled: true } pipeline) return null;
        if (string.IsNullOrWhiteSpace(pipeline.TargetAccountId)) return null;
        if (pipeline.Classes?.Contains("image", StringComparer.Ordinal) != true) return null;

        var built = (pipeline.Repositories ?? new List<PipelineRepositoryConfig>()).Any(r =>
            string.Equals(r.Class, "image", StringComparison.Ordinal)
            && (r.Artifacts ?? new List<string>()).Contains(serviceName, StringComparer.Ordinal));
        if (!built) return null;

        return new Lz.Aws.Compute.PipelineImageSource(
            $"{pipeline.TargetAccountId}.dkr.ecr.{region}.amazonaws.com", For(config, serviceName, tenantKey));
    }

    /// <summary>
    /// True when this system has opted into neutral naming. DEFAULTS TO FALSE — an absent Pipeline
    /// block, or a Registry block that does not name a mode, keeps today's name, so nothing about
    /// an un-opted-in system moves.
    /// </summary>
    public static bool IsNeutral(SystemConfig config)
        => string.Equals(config.Pipeline?.Registry?.RepositoryNaming, "neutral",
                         StringComparison.Ordinal);
}
