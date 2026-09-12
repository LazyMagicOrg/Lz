using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Lz.Aws.Pipeline;

/// <summary>What the ECS lifecycle hook tells ECS to do.</summary>
public enum HookStatus
{
    /// <summary>ECS continues the deployment.</summary>
    SUCCEEDED,

    /// <summary>ECS rolls the deployment back to the last successful service revision.</summary>
    FAILED,
}

/// <summary>The outcome of verifying one image reference.</summary>
public sealed record ImageVerification(string Image, bool Verified, string Detail);

/// <summary>An image reference parsed from a task definition, or the reason it cannot be verified.</summary>
public sealed record ImageReference(string Raw, string? DigestReference, string? Refusal);

/// <summary>The fields of an ECS lifecycle-hook invocation this hook acts on.</summary>
public sealed record HookEvent(
    string? ExecutionId, string LifecycleStage, string ServiceArn, string TargetServiceRevisionArn);

/// <summary>
/// The decisions the ECS <c>PRE_SCALE_UP</c> signature hook makes (DecoupledCd.md §4.4.1), as pure
/// functions. The hook Lambda fetches the task definition and runs the Notation CLI; everything that
/// DECIDES — which images, what trust, whether a result counts as verified, and what to tell ECS —
/// lives here where it can be tested exhaustively.
///
/// <para>WHOSE CRYPTOGRAPHY: the Notation CLI's, by decision (§11.17). This code builds the trust
/// policy and interprets the verifier's answer; it never implements signature verification itself.
/// Hand-rolled verification of the Notary Project format is how a checker ends up accepting a
/// signature it should refuse.</para>
///
/// <para>EVERY DECISION HERE FAILS CLOSED, and ECS agrees: a hook that does not return a valid
/// <c>hookStatus</c>, or that fails, rolls the deployment back. So an exception in the hook is
/// already a refusal at the platform. What this class must never do is return
/// <see cref="HookStatus.SUCCEEDED"/> for anything short of every image positively verified.</para>
///
/// <para>THE NOTATION FACTS BELOW WERE READ FROM ITS SOURCE, at the release the hook bundles
/// (notation v1.3.2, notation-go v1.3.2), not taken from documentation — each is cited where it is
/// used. They are the contract this file depends on, so a Notation upgrade means re-reading them.</para>
/// </summary>
public static class SignatureHook
{
    /// <summary>The only lifecycle stage this hook is written for.</summary>
    public const string Stage = "PRE_SCALE_UP";

    /// <summary>The trust store AWS Signer's root certificate is provisioned under.</summary>
    public const string SignerTrustStore = "signingAuthority:aws-signer-ts";

    /// <summary>The AWS Signer plugin's name, which Notation also uses for its directory.</summary>
    public const string SignerPluginName = "com.amazonaws.signer.notation.plugin";

    private static readonly JsonSerializerOptions PolicyJson = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // registry/repository@sha256:<64 hex>. Anchored, and the digest length is exact.
    private static readonly Regex DigestRef =
        new(@"^(?<repo>[^@\s]+)@sha256:(?<hex>[0-9a-f]{64})$", RegexOptions.Compiled);

    /// <summary>
    /// The Notation trust policy for this deployment.
    ///
    /// <para><c>level: strict</c> is the only acceptable value and it is not configurable here.
    /// notation-go defines four levels (<c>strict</c>, <c>permissive</c>, <c>audit</c>,
    /// <c>skip</c>); the weaker three log rather than enforce some checks — revocation among them —
    /// and an admission hook that could be configured to merely log is not a gate.</para>
    ///
    /// <para><c>trustedIdentities</c> are exactly the profiles passed in, never a wildcard. A trust
    /// policy that trusted <c>*</c> would accept any AWS Signer signature from any account — a
    /// signature would then prove only that someone, somewhere, signed it.</para>
    /// </summary>
    /// <param name="trustedProfileArns">
    /// UNVERSIONED signing-profile ARNs, the form AWS's documented trust policy uses. The signature
    /// itself carries a versioned ARN (…/scu_build_ci_scutaraservice/G27h7wDrRD, measured); that the
    /// match tolerates versions is documented, not yet measured — §4.4.1, unverified 3.
    /// </param>
    /// <param name="registryScopes">
    /// The repositories this policy governs, as notation-go requires them: "a fully qualified
    /// repository without the scheme, protocol or tag". Scoped rather than <c>*</c>, so the policy
    /// says which images it governs instead of silently governing everything.
    /// </param>
    public static string TrustPolicy(IReadOnlyList<string> trustedProfileArns, IReadOnlyList<string> registryScopes)
    {
        if (trustedProfileArns is not { Count: > 0 })
            throw new InvalidOperationException(
                "a trust policy needs at least one trusted identity. With none, every image is " +
                "refused — and the tempting fix, a wildcard, would accept any signature at all.");

        foreach (var arn in trustedProfileArns)
        {
            if (arn.Contains('*'))
                throw new InvalidOperationException(
                    $"trusted identity '{arn}' contains a wildcard. That would trust signatures from " +
                    "profiles nobody named, which is the one thing a signature check exists to prevent.");

            if (!arn.StartsWith("arn:aws:signer:", StringComparison.Ordinal)
                || !arn.Contains(":/signing-profiles/", StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"'{arn}' is not an AWS Signer signing-profile ARN " +
                    "(arn:aws:signer:<region>:<account>:/signing-profiles/<name>).");
        }

        ValidateScopes(registryScopes);

        return JsonSerializer.Serialize(new
        {
            // notation-go's only supported policy version: supportedPolicyVersions = {"1.0"}.
            version = "1.0",
            trustPolicies = new[]
            {
                new
                {
                    name = "lz-pipeline",
                    registryScopes,
                    signatureVerification = new { level = "strict" },
                    trustStores = new[] { SignerTrustStore },
                    trustedIdentities = trustedProfileArns,
                },
            },
        }, PolicyJson);
    }

    /// <summary>
    /// Refuse a scope list Notation would reject, at plan time rather than at the first deploy.
    /// Notation's own rules: at least one scope, and each a fully qualified repository without
    /// scheme or tag. This adds one of its own — no <c>*</c> — for the reason on
    /// <see cref="TrustPolicy"/>.
    /// </summary>
    public static void ValidateScopes(IReadOnlyList<string>? registryScopes)
    {
        if (registryScopes is not { Count: > 0 })
            throw new InvalidOperationException(
                "a trust policy needs at least one registry scope; Notation rejects a statement with " +
                "zero.");

        foreach (var scope in registryScopes)
        {
            var reason =
                string.IsNullOrWhiteSpace(scope) ? "it is empty"
                : scope == "*" || scope.Contains('*') ? "it is a wildcard, so the policy would govern images nobody named"
                : scope.Contains("://", StringComparison.Ordinal) ? "it carries a scheme"
                : scope.Contains('@') ? "it names a digest; a scope is a repository"
                : scope.LastIndexOf(':') > scope.LastIndexOf('/') ? "it names a tag; a scope is a repository"
                : !scope.Contains('/') ? "it is not registry/repository"
                : null;

            if (reason != null)
                throw new InvalidOperationException(
                    $"registry scope '{scope}' is not usable: {reason}. Notation wants a fully " +
                    "qualified repository without the scheme, protocol or tag.");
        }
    }

    /// <summary>
    /// Parse the images a task definition runs, refusing any that cannot be verified soundly.
    ///
    /// <para>A TAG IS REFUSED, not resolved. <c>notation verify repo:tag</c> resolves the tag to a
    /// digest at verify time — and ECS resolves it again, separately, when it pulls. Between the two a
    /// tag can move, so the hook would verify one image and ECS would run another. Notation says the
    /// same about itself: it warns to always use a digest "because tags are mutable and a tag
    /// reference can point to a different artifact than the one signed". This system pins by digest
    /// already (Rollback.PinImageDigest), so a tag here means something upstream went wrong.</para>
    ///
    /// <para>AN IMAGE OUTSIDE THE SCOPES IS REFUSED HERE TOO, even though Notation would refuse it
    /// ("no applicable trust policy"). Refusing first makes the evidence say what was wrong instead of
    /// relaying a verifier error that reads like a broken install.</para>
    /// </summary>
    public static IReadOnlyList<ImageReference> ImagesFrom(
        IEnumerable<string?>? containerImages, IReadOnlyList<string> registryScopes)
    {
        var result = new List<ImageReference>();

        foreach (var raw in containerImages ?? Array.Empty<string?>())
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                result.Add(new ImageReference(raw ?? "", null,
                    "a container has no image, so there is nothing to verify and it is refused."));
                continue;
            }

            var m = DigestRef.Match(raw);
            if (!m.Success)
            {
                result.Add(new ImageReference(raw, null,
                    raw.Contains("@sha256:", StringComparison.Ordinal)
                        ? $"'{raw}' has a malformed digest. A sha256 digest is exactly 64 lowercase hex characters."
                        : $"'{raw}' names a TAG, not a digest. The hook would verify the image the tag points " +
                          "at now, and ECS would pull whatever it points at later — they need not be the same " +
                          "bytes. Deploy by digest."));
                continue;
            }

            var repository = m.Groups["repo"].Value;
            if (!registryScopes.Contains(repository, StringComparer.Ordinal))
            {
                result.Add(new ImageReference(raw, null,
                    $"'{raw}' is in repository '{repository}', which this environment's trust policy " +
                    $"does not govern ([{string.Join(", ", registryScopes)}]). Only images from the " +
                    "pipeline's own repositories are admitted."));
                continue;
            }

            result.Add(new ImageReference(raw, raw, null));
        }

        return result;
    }

    /// <summary>
    /// Interpret the Notation CLI's result for one image.
    ///
    /// <para>POSITIVE CONFIRMATION ONLY. Verified means exit code 0 AND the success line naming this
    /// exact reference. An exit 0 without it is treated as unverified — a wrapper, a changed output
    /// format, or a truncated stream must not be read as a pass. The price is that a Notation release
    /// changing its wording would turn deploys red, which is the correct direction to fail in and is
    /// noticed at once.</para>
    ///
    /// <para>WHY AN EXACT STRING MATCH IS SOUND, from notation v1.3.2's source:
    /// <c>fmt.Println("Successfully verified signature for", printout)</c>, where the printout for a
    /// registry reference is <c>ref.Registry + "/" + ref.Repository + "@" + digest</c> — and
    /// <c>resolveReference</c> refuses outright when a digest the caller supplied differs from the one
    /// the registry resolves. For a canonical <c>registry/repository@sha256:…</c> input, the printed
    /// reference is therefore the input, character for character.</para>
    /// </summary>
    public static ImageVerification InterpretNotation(string image, int exitCode, string? stdout, string? stderr)
    {
        var output = (stdout ?? "") + "\n" + (stderr ?? "");

        if (exitCode != 0)
            return new ImageVerification(image, false, $"notation exited {exitCode}: {Trim(output)}");

        var expected = $"Successfully verified signature for {image}";
        var confirmed = (stdout ?? "")
            .Split('\n')
            .Any(line => string.Equals(line.TrimEnd('\r'), expected, StringComparison.Ordinal));

        if (!confirmed)
            return new ImageVerification(image, false,
                "notation exited 0 but did not report verifying this exact reference, so it is not " +
                $"counted as verified. Output: {Trim(output)}");

        return new ImageVerification(image, true, "signature verified against the trust policy");
    }

    /// <summary>
    /// What to tell ECS.
    ///
    /// <para>SUCCEEDED only when there is at least one image and EVERY image verified. An empty list is
    /// FAILED: a task definition with nothing to verify is not a clean bill of health, it is a gap the
    /// check cannot see into.</para>
    /// </summary>
    public static (HookStatus Status, string Reason) Decide(
        IReadOnlyList<ImageReference> images, IReadOnlyList<ImageVerification> verifications)
    {
        if (images.Count == 0)
            return (HookStatus.FAILED, "the task definition names no images, so nothing could be verified.");

        var refused = images.Where(i => i.Refusal != null).ToList();
        if (refused.Count > 0)
            return (HookStatus.FAILED, string.Join(" | ", refused.Select(r => r.Refusal)));

        foreach (var image in images)
        {
            var v = verifications.FirstOrDefault(
                x => string.Equals(x.Image, image.DigestReference, StringComparison.Ordinal));

            if (v is null)
                return (HookStatus.FAILED,
                    $"'{image.DigestReference}' was never verified. Every image must be checked; a missing " +
                    "result is a refusal, not a pass.");

            if (!v.Verified)
                return (HookStatus.FAILED, v.Detail);
        }

        return (HookStatus.SUCCEEDED, $"all {images.Count} image(s) verified");
    }

    /// <summary>
    /// Read the hook invocation. Anything missing is a refusal: an event this hook cannot understand is
    /// not one it may wave through.
    /// </summary>
    public static HookEvent ParseEvent(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"the hook event is not valid JSON: {ex.Message}", ex);
        }

        using var owned = doc;
        var root = owned.RootElement;

        string Required(JsonElement parent, string name, string path)
        {
            if (parent.ValueKind != JsonValueKind.Object
                || !parent.TryGetProperty(name, out var v)
                || v.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(v.GetString()))
                throw new InvalidOperationException($"the hook event has no '{path}'.");
            return v.GetString()!;
        }

        var stage = Required(root, "lifecycleStage", "lifecycleStage");

        // REFUSED AT ANY OTHER STAGE. Later stages run after new tasks have started, when admission
        // is already too late; a hook attached there is a misconfiguration, and loud beats quietly
        // checking something that has already been scheduled.
        if (!string.Equals(stage, Stage, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"the hook was invoked at '{stage}'. It is written for {Stage}, the only stage that runs " +
                "before any new task is scheduled; attach it there.");

        root.TryGetProperty("executionDetails", out var details);
        var revision = Required(details, "targetServiceRevisionArn", "executionDetails.targetServiceRevisionArn");
        var service = Required(details, "serviceArn", "executionDetails.serviceArn");

        string? executionId = root.TryGetProperty("executionId", out var e) && e.ValueKind == JsonValueKind.String
            ? e.GetString()
            : null;

        return new HookEvent(executionId, stage, service, revision);
    }

    /// <summary>
    /// The response body. ONLY <c>hookStatus</c>: the reason goes to the function's log, not the
    /// response, because a response field ECS does not accept would itself be an invalid response —
    /// which ECS treats as a failure, turning every admitted deploy into a rollback.
    /// </summary>
    public static string Response(HookStatus status) =>
        JsonSerializer.Serialize(new { hookStatus = status.ToString() });

    /// <summary>
    /// Split an ECR authorization token into the credentials Notation reads from
    /// <c>NOTATION_USERNAME</c> and <c>NOTATION_PASSWORD</c>. The token is base64 of
    /// <c>AWS:&lt;password&gt;</c>; the password itself may contain colons, so only the first
    /// separates.
    /// </summary>
    public static (string Username, string Password) DecodeRegistryToken(string authorizationToken)
    {
        string decoded;
        try
        {
            decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(authorizationToken));
        }
        catch (FormatException)
        {
            // The token is a credential: the message names the problem and never the value.
            throw new InvalidOperationException("the registry authorization token is not base64.");
        }

        var colon = decoded.IndexOf(':');
        if (colon <= 0 || colon == decoded.Length - 1)
            throw new InvalidOperationException(
                "the registry authorization token is not 'user:password'.");

        return (decoded[..colon], decoded[(colon + 1)..]);
    }

    private static string Trim(string s)
    {
        var t = s.Trim().Replace('\n', ' ');
        return t.Length <= 400 ? t : t[..400] + "…";
    }
}

/// <summary>
/// Where Notation looks for everything, rooted in a writable directory.
///
/// <para>WHY A LAYOUT AT ALL: Lambda's package directory is read-only, and Notation both reads its
/// configuration from, and WRITES a revocation cache to, directories derived from the user's home.
/// Every path here comes from notation-go v1.3.2's <c>dir</c> package, read from source:
/// the config directory is <c>os.UserConfigDir()/notation</c> (on Linux <c>$XDG_CONFIG_HOME</c>,
/// else <c>$HOME/.config</c>); plugins sit under the config directory, because
/// <c>UserLibexecDir</c> defaults to it; the cache is <c>os.UserCacheDir()/notation</c> with CRLs
/// under <c>crl</c>; and the trust policy file is <c>trustpolicy.json</c>. Get one of these wrong and
/// the failure is not a refusal with a reason — it is Notation reporting a missing policy or plugin,
/// which reads like a broken install.</para>
///
/// <para>Paths are joined with <c>/</c> explicitly: they describe a Linux filesystem, and this also
/// runs in tests on Windows, where <see cref="Path.Combine(string, string)"/> would write
/// backslashes.</para>
/// </summary>
public sealed record NotationLayout(
    string Root,
    string Home,
    string ConfigHome,
    string CacheHome,
    string ConfigDirectory,
    string TrustPolicyPath,
    string TrustStoreDirectory,
    string PluginDirectory,
    string PluginPath,
    string NotationPath)
{
    /// <summary>The folder inside the deployment package the verifier's files are shipped in.</summary>
    public const string PackageFolder = "notation";

    /// <summary>The Notation CLI binary, as shipped.</summary>
    public const string NotationFile = "notation";

    /// <summary>The AWS Signer plugin binary. Notation requires the name <c>notation-{plugin name}</c>.</summary>
    public static readonly string PluginFile = $"notation-{SignatureHook.SignerPluginName}";

    /// <summary>AWS Signer's root certificate for the commercial partition, as AWS names the download.</summary>
    public const string RootCertificateFile = "aws-signer-notation-root.cert";

    /// <summary>The verifier's files as zip entry names, which always use <c>/</c>.</summary>
    public static IReadOnlyList<string> PackagedFiles => new[]
    {
        $"{PackageFolder}/{NotationFile}",
        $"{PackageFolder}/{PluginFile}",
        $"{PackageFolder}/{RootCertificateFile}",
    };

    /// <summary>Which of the verifier's files a package lacks. Empty means it can verify.</summary>
    public static IReadOnlyList<string> MissingFromPackage(IEnumerable<string> entryNames)
    {
        var present = entryNames.Select(e => e.Replace('\\', '/')).ToHashSet(StringComparer.Ordinal);
        return PackagedFiles.Where(f => !present.Contains(f)).ToList();
    }

    public static NotationLayout Under(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !root.StartsWith('/'))
            throw new InvalidOperationException(
                $"'{root}' is not an absolute Linux path; the layout describes the Lambda filesystem.");

        root = root.TrimEnd('/');
        var configHome = $"{root}/config";
        var config = $"{configHome}/notation";

        // "signingAuthority:aws-signer-ts" is {store type}:{store name}, and the store lives at
        // truststore/x509/{type}/{name} — derived from the constant the trust policy names, so the
        // policy and the directory cannot disagree.
        var parts = SignatureHook.SignerTrustStore.Split(':');
        var plugins = $"{config}/plugins/{SignatureHook.SignerPluginName}";

        return new NotationLayout(
            Root: root,
            Home: $"{root}/home",
            ConfigHome: configHome,
            CacheHome: $"{root}/cache",
            ConfigDirectory: config,
            TrustPolicyPath: $"{config}/trustpolicy.json",
            TrustStoreDirectory: $"{config}/truststore/x509/{parts[0]}/{parts[1]}",
            PluginDirectory: plugins,
            PluginPath: $"{plugins}/{PluginFile}",
            NotationPath: $"{root}/bin/{NotationFile}");
    }

    /// <summary>
    /// The environment the Notation process runs with. <c>HOME</c> is set as well as the XDG
    /// variables because Lambda defines no home directory, and Go's <c>os.UserConfigDir</c> fails
    /// outright when neither is present. The registry credentials are read by notation from
    /// <c>NOTATION_USERNAME</c>/<c>NOTATION_PASSWORD</c> (its <c>SecureFlagOpts</c>), which keeps the
    /// password off the command line where a process listing would show it.
    /// </summary>
    public IReadOnlyDictionary<string, string> Environment(string username, string password) =>
        new Dictionary<string, string>
        {
            ["HOME"] = Home,
            ["XDG_CONFIG_HOME"] = ConfigHome,
            ["XDG_CACHE_HOME"] = CacheHome,
            ["NOTATION_USERNAME"] = username,
            ["NOTATION_PASSWORD"] = password,
        };
}
