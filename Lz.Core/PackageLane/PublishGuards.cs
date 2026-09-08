using System.Xml.Linq;

namespace Lz.Core.PackageLane;

/// <summary>What a package about to be pushed says about itself, read from its own nuspec.</summary>
public readonly record struct PackageFacts(string Id, string Version, string? RepositoryCommit, string FileName);

/// <summary>What the registry already holds at one id@version. <c>null</c> means nothing is there.</summary>
public readonly record struct PublishedPackage(string? RepositoryCommit);

/// <summary>
/// One package's answer to "may this be pushed?". Two of these are not refusals, and the difference
/// matters operationally: <see cref="AlreadyPublishedFromThisCommit"/> is a re-run of a workflow that
/// already succeeded, which is a fact about history rather than a mistake to stop.
/// </summary>
public enum PublishVerdict
{
    /// <summary>Nothing objects.</summary>
    Publishable,

    /// <summary>
    /// This exact id@version is already in the registry, published from THIS commit - so a re-run,
    /// not a collision. Not refused. The push still fails, because <c>--skip-duplicate</c> stays off
    /// deliberately (SdlcVersioning 8.2): the registry's own rejection is the authority on what it
    /// already holds, and hiding it would hide a real collision too. The verdict exists so the
    /// operator can tell the two failures apart without reading the registry by hand.
    /// </summary>
    AlreadyPublishedFromThisCommit,

    /// <summary>
    /// The version carries Nerdbank.GitVersioning's commit-id discriminator, so it was NOT built as a
    /// public release. Guard two.
    /// </summary>
    NotAPublicRelease,

    /// <summary>The nuspec records no <c>&lt;repository commit&gt;</c>, so nothing here can be checked.</summary>
    CommitNotRecorded,

    /// <summary>Built from a different commit than the one being published - a stale or foreign artifact.</summary>
    BuiltFromAnotherCommit,

    /// <summary>Already in the registry at this version, from a DIFFERENT commit. Guard one's real catch.</summary>
    CollidesWithADifferentCommit,

    /// <summary>
    /// Already in the registry, but what is there records no commit, so a re-run cannot be told from a
    /// collision. Refused rather than guessed.
    /// </summary>
    CollidesAndCannotBeCompared,

    /// <summary>The registry could not be asked - unreachable, unauthorised, or no V3 base address.</summary>
    RegistryUndeterminable,
}

/// <summary>
/// The two publish guards from <c>SdlcVersioning</c> §4.6, as pure decisions over facts read from the
/// artifacts themselves.
///
/// <para><b>Why these live under <c>PackageLane</c>.</b> Partly because a publish is the moment the
/// published lane is written, and partly for a duller reason worth recording: the obvious folder name
/// is unusable. This repo's <c>.gitignore</c> carries the stock <c>publish/</c> rule for
/// <c>dotnet publish</c> output, Windows matches it case-insensitively, and a <c>Lz.Core/Publish/</c>
/// folder is therefore ignored in full - three source files that compile locally, are absent from a
/// clean clone, and break CI on the next push. Caught here by <c>git status</c> before the commit,
/// which is the only place it is visible.</para>
///
/// <para><b>Guard two reads the artifact, not an MSBuild property.</b> The row that specifies it says
/// "refuse unless NBGV reports <c>PublicRelease == true</c>", and this is where NBGV records that
/// answer: a non-public build gets the commit-id discriminator appended to its version, a public one
/// does not. Reading the nupkg is strictly stronger than querying the property, because a workflow
/// builds and pushes in separate steps and it is the bytes in the feed - not a property re-evaluated
/// later, possibly against a different ref - that reach the registry. Measured in this workspace on
/// 2026-09-07: LazyMagic off <c>dev</c> mints <c>3.0.23-g6fc0b7081e</c>, Lz and Service off
/// <c>main</c> mint <c>0.12.6</c> and <c>1.0.3</c>.</para>
///
/// <para><b>The specification's own literal is dead and is not implemented.</b> It named a
/// <c>-local</c> prerelease marker from the pre-NBGV design; nothing emits that, so a guard written to
/// it would be green in both states and prove nothing.</para>
///
/// <para><b>Residual gap, stated rather than hidden:</b> an artifact whose version was hand-set - as
/// LazyMagic's <c>&lt;Version&gt;3.0.17&lt;/Version&gt;</c> was until 2026-09-06 - carries no
/// discriminator and passes guard two, because there is no signal in a nupkg for "NBGV was used".
/// Guard one covers that case from the other side: a reverted version is one the registry has already
/// seen at a different commit.</para>
/// </summary>
public static class PublishGuards
{
    /// <summary>
    /// Guard two, plus the artifact-identity check. Runs first and alone decides a refusal, because a
    /// non-public build must not be pushed whatever the registry holds.
    /// </summary>
    /// <param name="expectedCommit">
    /// The commit being published - <c>github.sha</c> in CI. Optional, and its absence is reported by
    /// the caller rather than passed over silently: a check that quietly does not run is the failure
    /// mode this whole row exists to correct.
    ///
    /// <para>It earned its place on a shared feed: <c>repos/Packages</c> held Service's and
    /// BaseAppLib's output in one directory, so a glob push from either repo would have published the
    /// other's packages under its own name, and every foreign package failed this check. That feed was
    /// split per producer on 2026-09-08, which retires the hazard by construction - but the check
    /// stays, because it also catches the commoner case: a stale artifact from an earlier build.</para>
    /// </param>
    public static PublishVerdict Artifact(PackageFacts package, string? expectedCommit)
    {
        // Same predicate as the sync refusal, deliberately: both ask NBGV's one question - was this
        // built as a public release? - and one answer must not drift into two implementations.
        if (PackageLaneSync.CarriesACommitId(package.Version))
            return PublishVerdict.NotAPublicRelease;

        if (string.IsNullOrWhiteSpace(package.RepositoryCommit))
            return PublishVerdict.CommitNotRecorded;

        if (!string.IsNullOrWhiteSpace(expectedCommit) &&
            !SameCommit(package.RepositoryCommit!, expectedCommit!))
            return PublishVerdict.BuiltFromAnotherCommit;

        return PublishVerdict.Publishable;
    }

    /// <summary>
    /// Guard one. Compares the published nuspec's commit rather than the version string alone, so a
    /// re-run of a workflow is distinguishable from two different trees claiming one version.
    /// </summary>
    /// <param name="published">What the registry holds at this id@version, or null if nothing.</param>
    public static PublishVerdict Registry(PackageFacts package, PublishedPackage? published)
    {
        if (published is null) return PublishVerdict.Publishable;

        var theirs = published.Value.RepositoryCommit;
        if (string.IsNullOrWhiteSpace(theirs)) return PublishVerdict.CollidesAndCannotBeCompared;
        if (string.IsNullOrWhiteSpace(package.RepositoryCommit)) return PublishVerdict.CollidesAndCannotBeCompared;

        return SameCommit(package.RepositoryCommit!, theirs!)
            ? PublishVerdict.AlreadyPublishedFromThisCommit
            : PublishVerdict.CollidesWithADifferentCommit;
    }

    /// <summary>
    /// Whether a verdict stops the push. Two verdicts are not refusals - the clean one, and the benign
    /// re-run - and everything else is, including the two "could not tell" states: this guard fails
    /// closed, because the thing it protects is irreversible.
    /// </summary>
    public static bool Refuses(PublishVerdict verdict) =>
        verdict is not (PublishVerdict.Publishable or PublishVerdict.AlreadyPublishedFromThisCommit);

    /// <summary>
    /// Two commit ids naming one commit. Case-insensitive, and an abbreviation of at least seven hex
    /// digits matches the full id it prefixes - <c>git rev-parse --short</c> and <c>github.sha</c> are
    /// both in circulation, and refusing on their difference would be a false alarm rather than a
    /// catch. Seven is git's own floor for an abbreviated id.
    /// </summary>
    public static bool SameCommit(string a, string b)
    {
        if (!IsHex(a) || !IsHex(b)) return false;
        var (shorter, longer) = a.Length <= b.Length ? (a, b) : (b, a);
        if (shorter.Length < 7) return false;
        return longer.StartsWith(shorter, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHex(string s) => s.Length > 0 && s.All(Uri.IsHexDigit);

    /// <summary>
    /// The facts a nuspec states about itself. Matched on local names, because the nuspec schema
    /// namespace varies by the SDK that wrote it and a namespace-qualified read would silently find
    /// nothing on the ones it does not expect.
    /// </summary>
    public static PackageFacts ParseNuspec(string xml, string fileName)
    {
        var doc = XDocument.Parse(xml);
        var metadata = doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "metadata")
            ?? throw new InvalidDataException($"{fileName}: nuspec has no <metadata> element.");

        string? Value(string name) =>
            metadata.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value?.Trim();

        var id = Value("id");
        var version = Value("version");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(version))
            throw new InvalidDataException($"{fileName}: nuspec has no <id> or no <version>.");

        var commit = metadata.Elements()
            .FirstOrDefault(e => e.Name.LocalName == "repository")?
            .Attribute("commit")?.Value?.Trim();

        return new PackageFacts(id!, version!, string.IsNullOrWhiteSpace(commit) ? null : commit, fileName);
    }

    /// <summary>A line for the report, in the operator's terms rather than the enum's.</summary>
    public static string Explain(PublishVerdict verdict, PackageFacts package, string? expectedCommit) => verdict switch
    {
        PublishVerdict.Publishable => "ok",
        PublishVerdict.AlreadyPublishedFromThisCommit =>
            "already published FROM THIS COMMIT - a re-run, not a collision. The push will still fail: " +
            "--skip-duplicate stays off so the registry's rejection stays visible.",
        PublishVerdict.NotAPublicRelease =>
            $"NOT a public release - the version carries NBGV's commit-id discriminator, so this build " +
            $"was not made from the public-release ref. Pass -p:PublicRelease=true only when the ref " +
            $"genuinely is one; NBGV reads the ambient GITHUB_REF otherwise.",
        PublishVerdict.CommitNotRecorded =>
            "the nuspec records no <repository commit>, so neither guard can check anything about it.",
        PublishVerdict.BuiltFromAnotherCommit =>
            $"built from {Short(package.RepositoryCommit)}, not {Short(expectedCommit)} - a stale artifact " +
            $"from an earlier build, or another repo's package sharing this feed.",
        PublishVerdict.CollidesWithADifferentCommit =>
            "COLLISION - the registry already holds this version, published from a different commit.",
        PublishVerdict.CollidesAndCannotBeCompared =>
            "the registry already holds this version and records no commit for it, so a re-run cannot be " +
            "told from a collision. Refused rather than guessed.",
        PublishVerdict.RegistryUndeterminable =>
            "the registry could not be asked, so guard one did not run. Refused rather than assumed clear.",
        _ => verdict.ToString(),
    };

    private static string Short(string? commit) =>
        string.IsNullOrWhiteSpace(commit) ? "(none)" : commit!.Length <= 10 ? commit! : commit![..10];
}
