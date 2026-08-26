using Lz.Core.Config;

namespace Lz.Tests.Orchestration.Tests;

/// <summary>
/// The full-system teardown–redeploy drill (TEARDOWN-REDEPLOY-PUNCHLIST.md).
///
/// Proves that `lz destroytenant` + `lz destroysystem` leave NO stack
/// resource behind that would block a subsequent deploy, that the persistent
/// layer survives untouched, and that the redeploy sequence restores a
/// healthy system. Interrogates live state first and always ends in the
/// beginning state:
///   deployed     → destroy → verify-clean → redeploy → verify-healthy
///   not deployed → deploy  → verify-healthy → destroy → verify-clean
///
/// SAFETY: dev-only. <see cref="DevEnvironmentGuard"/> hard-fails the run on
/// any non-dev signal BEFORE the first lz invocation (B0). On top of that,
/// the destructive phases only execute when LZ_LIFECYCLE_ENABLE=1 — without
/// it the test performs the read-only interrogation and reports the branch
/// it WOULD take, then skips (house convention: return, don't fail).
///
/// Cost when enabled: destroys the live dev environment; ~60–120 min
/// (CloudFront delete alone is 20–40); all Cognito users in both pools are
/// lost. Run: dotnet test --filter Category=Integration with an lzm-dev SSO
/// session (`aws sso login --profile lzm-dev`).
/// </summary>
[Collection("LifecycleDrill")]
[Trait("Category", "Integration")]
public class TeardownRedeployTests
{
    private static readonly TimeSpan DeployTimeout = TimeSpan.FromMinutes(60);
    private static readonly TimeSpan DestroyTimeout = TimeSpan.FromMinutes(90);

    /// <summary>
    /// The tenant to drill. LZ_LIFECYCLE_TENANT wins (verbatim — the caller validates its shape
    /// and that a config exists). Otherwise it is systemconfig's <c>TestTenant</c>, resolved
    /// through <see cref="TestTenancy"/> — the same authority <c>lz gettesttenant</c> renders and
    /// consuming systems' test tiers read, so the drill and those tiers cannot disagree about
    /// which tenant is the expendable one.
    ///
    /// <para>NOT INFERRED. This used to default to "mp" (the origin repo's tenant), which was
    /// meaningless in a consuming repo whose tenant differs and made the suite red by default.
    /// The first fix discovered the tenant from the repo's single
    /// <c>tenantconfig.{sk}.*.dev.yaml</c> — better, but still a guess, and this key is
    /// interpolated into <c>lz destroytenant</c>. A repo that grows a second tenantconfig would
    /// have silently changed which tenant "the only one" meant. An explicit key cannot drift that
    /// way, so a system that has not named one is told to, and skips.</para>
    ///
    /// <para>Returns "" when it cannot resolve — the caller then skips with <see cref="TenantKeyReason"/>.</para>
    /// </summary>
    private static string TenantKey => ResolveTenant().Key;

    /// <summary>Why <see cref="TenantKey"/> is empty; surfaced verbatim in the skip.</summary>
    private static string TenantKeyReason => ResolveTenant().Reason;

    private static (string Key, string Reason) ResolveTenant()
    {
        var overrideKey = Environment.GetEnvironmentVariable("LZ_LIFECYCLE_TENANT");
        if (!string.IsNullOrWhiteSpace(overrideKey))
            return (overrideKey, "");

        var root = LifecycleHarness.FindRepoRoot();
        var sysConfigs = Directory.GetFiles(root, "systemconfig.*.dev.yaml");
        if (sysConfigs.Length != 1)
            return ("", $"expected exactly one systemconfig.*.dev.yaml in {root}, " +
                        $"found {sysConfigs.Length} — cannot tell which system to drill.");

        SystemConfig config;
        try { config = ConfigLoader.LoadSystemConfig(sysConfigs[0]); }
        catch (Exception ex)
        {
            return ("", $"could not load {Path.GetFileName(sysConfigs[0])}: {ex.Message}");
        }

        return TestTenancy.TryResolve(config, out var tenancy, out var why)
            ? (tenancy!.TenantKey, "")
            : ("", why);
    }
    private static bool DrillEnabled =>
        Environment.GetEnvironmentVariable("LZ_LIFECYCLE_ENABLE") == "1";

    [Fact]
    public async Task TeardownAndRedeploy_LeavesNoBlockingResidue()
    {
        var harness = new LifecycleHarness();

        // ------------------------------------------------------------------
        // B0 — dev-only guard. Hard FAIL (never skip) on any non-dev signal,
        // before anything else runs.
        // ------------------------------------------------------------------
        // Resolve the environment exactly the way lz does: from the repo-root
        // folder hierarchy (_Dev_ → dev). Anchored on the repo root explicitly —
        // never via process-wide CurrentDirectory, which would race with the
        // parallel Config tests. (lz itself gets the same anchoring because the
        // harness launches it with WorkingDirectory = repo root.)
        var resolvedEnv = ConfigResolver.ResolveEnvironment(null, harness.RepoRoot);
        string? systemConfigFile = null;
        try { systemConfigFile = harness.SystemConfigFile("dev"); }
        catch (InvalidOperationException) { /* guard reports it below */ }

        var violations = DevEnvironmentGuard.Violations(
            resolvedEnv, systemConfigFile, harness.RepoRoot);
        if (violations.Count > 0)
            Assert.Fail(
                "DEV-ONLY GUARD: refusing to run the teardown drill.\n  " +
                string.Join("\n  ", violations));

        // The tenant is interpolated into destructive lz command lines — resolve and
        // validate it BEFORE anything runs. An unresolved tenant is "not runnable here"
        // (skip, per this test's convention), not a failure. A resolved tenant must pass
        // strict key shape (no argument injection) and have a tenantconfig on disk (no
        // 60-minute deploy before discovering a typo).
        var sk = systemConfigFile != null
            ? Path.GetFileName(systemConfigFile).Split('.')[1] : "?";
        var (tenantKey, tenantReason) = ResolveTenant();
        if (string.IsNullOrEmpty(tenantKey))
        {
            Skip(harness, $"cannot resolve a tenant to drill: {tenantReason}");
            return;
        }
        Assert.True(System.Text.RegularExpressions.Regex.IsMatch(tenantKey, "^[a-z0-9-]{1,32}$"),
            $"tenant key '{tenantKey}' is not a valid tenant key " +
            "(expected ^[a-z0-9-]{1,32}$).");
        var tenantConfigPath = Path.Combine(
            harness.RepoRoot, $"tenantconfig.{sk}.{tenantKey}.dev.yaml");
        Assert.True(File.Exists(tenantConfigPath),
            $"tenant '{tenantKey}' has no {Path.GetFileName(tenantConfigPath)} " +
            "in the repo root — refusing to drill an unknown tenant.");

        // ------------------------------------------------------------------
        // B2 — preconditions (graceful skip; missing tools/creds are not
        // failures, they mean "not runnable here").
        // ------------------------------------------------------------------
        var version = await harness.RunLzAsync("--version", TimeSpan.FromMinutes(2), "version");
        if (!version.Ok)
        {
            Skip(harness, "lz tool not runnable (not installed / not on PATH).");
            return;
        }
        // --version prints "deploy plugin: (none — ...)" when no plugin loads,
        // so probe for the ABSENCE marker, not the label.
        if (version.StdOut.Contains("deploy plugin: (none"))
        {
            Skip(harness, "Deploy plugin not built/discovered (dotnet build Deploy first).");
            return;
        }
        // The drill must run THIS repo's packed lz — a stale global install
        // would silently test old destroy/verify semantics. The runner prints
        // the feed it resolved from; require it to live under the repo root.
        Assert.True(
            version.StdOut.Contains(Path.Combine(harness.LzRepoRoot, "Packages"),
                StringComparison.OrdinalIgnoreCase),
            "lz resolved its CLI from a feed OUTSIDE this repo — the drill would " +
            $"exercise the wrong lz build. --version said:\n{version.StdOut}");
        Console.WriteLine($"[lifecycle] artifacts: {harness.ArtifactsDir}");

        // ------------------------------------------------------------------
        // B3 — interrogate the live starting state (read-only lz verify).
        // A verify hard-failure here usually means expired SSO — skip.
        // ------------------------------------------------------------------
        LifecycleHarness.VerifySnapshot start;
        try
        {
            start = await harness.VerifyAsync();
        }
        catch (Exception ex)
        {
            Skip(harness, $"lz verify not runnable ({ex.Message}). " +
                          "Is the lzm-dev SSO session open?");
            return;
        }
        if (start.Errors > 0)
        {
            Skip(harness, $"{start.Errors} verify checks errored " +
                          "(expired SSO / throttling?) — refusing to drill on noisy data.");
            return;
        }

        // Partial state = neither cleanly deployed nor cleanly destroyed.
        // That is exactly the residue this test exists to catch — but as a
        // STARTING state it means a previous run (or manual op) left a mess.
        // Fail with the evidence; do not "repair" automatically.
        if (start.LooksPartial)
            Assert.Fail(
                $"Starting state is PARTIAL (stack: {start.StackPresent} present, " +
                $"{start.StackAbsent} absent, {start.StackTombstoned} tombstoned). " +
                $"Inspect {harness.ArtifactsDir} and reconcile manually before drilling.");

        var beganDeployed = start.LooksDeployed;
        Console.WriteLine($"[lifecycle] starting state: {(beganDeployed ? "DEPLOYED" : "NOT DEPLOYED")} " +
            $"(stack {start.StackPresent} present / {start.StackAbsent} absent; " +
            $"persistent {start.PersistentPresent} present)");

        if (!DrillEnabled)
        {
            Skip(harness,
                "read-only interrogation complete. Set LZ_LIFECYCLE_ENABLE=1 " +
                $"to run the destructive drill ({(beganDeployed ? "destroy→redeploy" : "deploy→destroy")}, " +
                "~60–120 min, destroys dev Cognito users).");
            return;
        }

        // Baseline for the persistent-layer diff: whatever exists now must
        // still exist after every subsequent phase.
        var persistentBaseline = start.PresentPersistentNames();

        // ------------------------------------------------------------------
        // The drill. Both branches end in the beginning state. On ANY phase
        // failure, capture the end state first — a half-destroyed system is
        // exactly when the resource map matters most.
        // ------------------------------------------------------------------
        try
        {
            if (beganDeployed)
            {
                await DestroyPhaseAsync(harness, persistentBaseline);
                await RedeployPhaseAsync(harness, persistentBaseline);
            }
            else
            {
                // Deploy first. Persistent resources the deploy CREATES
                // (tenant/BFF/subtenant tables, subtenant buckets) join the
                // baseline the destroy phase must then preserve.
                var postDeploy = await RedeployPhaseAsync(harness, persistentBaseline);
                await DestroyPhaseAsync(harness, postDeploy.PresentPersistentNames());
            }
        }
        catch
        {
            try
            {
                var final = await harness.VerifyAsync();
                Console.WriteLine(
                    $"[lifecycle] FAILURE end-state: stack {final.StackPresent} present / " +
                    $"{final.StackAbsent} absent / {final.StackTombstoned} tombstoned; " +
                    $"persistent {final.PersistentPresent} present. Artifacts: {harness.ArtifactsDir}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[lifecycle] FAILURE end-state capture also failed: {ex.Message}");
            }
            throw;
        }

        Console.WriteLine("[lifecycle] drill complete — system is back in its beginning state.");
    }

    /// <summary>
    /// xUnit hides Console output for passing tests, so a skip must ALSO leave
    /// a durable marker — otherwise a skipped run is indistinguishable from a
    /// real drill in the test report.
    /// </summary>
    private static void Skip(LifecycleHarness harness, string reason)
    {
        File.WriteAllText(Path.Combine(harness.ArtifactsDir, "SKIPPED.txt"), reason);
        Console.WriteLine($"[lifecycle] SKIP: {reason}");
    }

    // ----------------------------------------------------------------------
    // B4 + B5 — destroy tenant → refresh foundation state → destroy system,
    // then assert: every stack resource gone, no tombstones, persistent
    // layer intact.
    // ----------------------------------------------------------------------
    private static async Task DestroyPhaseAsync(
        LifecycleHarness harness, IReadOnlySet<string> persistentBaseline)
    {
        var t = await harness.RunLzAsync(
            $"destroytenant --tenantkey {TenantKey} --yes", DestroyTimeout, "destroytenant");
        Assert.True(t.Ok, $"destroytenant failed (exit {t.ExitCode}) — see artifacts.\n{t.StdErr}");

        // B4/B8: the apex A record + ACM validation CNAMEs live in BOTH
        // stacks' state (cross-stack double-ownership). Refresh the
        // foundation's state so records the tenant destroy already removed
        // are dropped from it before the foundation destroy runs.
        var refresh = await harness.RunLzAsync(
            "previewsystem --refresh", TimeSpan.FromMinutes(20), "refresh-foundation");
        Assert.True(refresh.Ok,
            $"previewsystem --refresh failed (exit {refresh.ExitCode}) — foundation state " +
            $"could not be reconciled after the tenant destroy.\n{refresh.StdErr}");

        var s = await harness.RunLzAsync("destroysystem --yes", DestroyTimeout, "destroysystem");
        Assert.True(s.Ok, $"destroysystem failed (exit {s.ExitCode}) — see artifacts.\n{s.StdErr}");

        // B5 — the core assertion: nothing stack-owned lingers (Absent
        // everywhere, and specifically NO Secrets Manager tombstone), while
        // everything persistent that existed at baseline still exists.
        var after = await harness.VerifyAsync();
        Assert.True(after.Errors == 0, $"{after.Errors} verify checks errored post-destroy.");
        Assert.True(after.StackTombstoned == 0,
            "Secrets Manager tombstone(s) survived destroy — the RecoveryWindowInDays=0 " +
            "path regressed; a redeploy would fail with 'already scheduled for deletion'.");
        Assert.True(after.StackPresent == 0,
            $"{after.StackPresent} stack resource(s) still present after destroy — " +
            $"orphaned residue that can block redeploy. See artifacts for the list.");

        var missing = persistentBaseline.Except(after.PresentPersistentNames()).ToList();
        Assert.True(missing.Count == 0,
            "Persistent layer was damaged by destroy — missing: " + string.Join(", ", missing));
    }

    // ----------------------------------------------------------------------
    // B6 + B7 — deploysystem → deploytenant → deployassets, then assert:
    // verify says deployed, previews are steady-state, HTTP smoke passes.
    // ----------------------------------------------------------------------
    private static async Task<LifecycleHarness.VerifySnapshot> RedeployPhaseAsync(
        LifecycleHarness harness, IReadOnlySet<string> persistentBaseline)
    {
        var sys = await harness.RunLzAsync("deploysystem", DeployTimeout, "deploysystem");
        Assert.True(sys.Ok, $"deploysystem failed (exit {sys.ExitCode}).\n{sys.StdErr}");

        var ten = await harness.RunLzAsync(
            $"deploytenant --tenantkey {TenantKey}", DeployTimeout, "deploytenant");
        Assert.True(ten.Ok, $"deploytenant failed (exit {ten.ExitCode}).\n{ten.StdErr}");

        // Required: the ForceDestroy'd assets buckets came back EMPTY —
        // only deployassets (syncing /Tenancies/) restores their content.
        var assets = await harness.RunLzAsync("deployassets", TimeSpan.FromMinutes(30), "deployassets");
        Assert.True(assets.Ok, $"deployassets failed (exit {assets.ExitCode}).\n{assets.StdErr}");

        // B7a — every expected stack resource is present again, AND the runtime
        // smoke gate passes. Asserting the tool's own expectMet (not just the
        // stack counts) means a failing smoke probe — e.g. the origin-verify gate
        // not engaged after redeploy — fails the drill instead of sailing through.
        var after = await harness.VerifyAsync("--expect deployed");
        Assert.True(after.Errors == 0, $"{after.Errors} verify checks errored post-redeploy.");
        Assert.True(after.LooksDeployed,
            $"Redeploy incomplete: {after.StackAbsent} stack resource(s) still absent, " +
            $"{after.StackTombstoned} tombstoned.");
        Assert.True(after.SmokeFailed == 0,
            $"{after.SmokeFailed} smoke probe(s) failing post-redeploy — surfaces unhealthy.");
        Assert.True(after.ExpectMet == true,
            "lz verify --expect deployed reported NOT MET post-redeploy.");

        var missing = persistentBaseline.Except(after.PresentPersistentNames()).ToList();
        Assert.True(missing.Count == 0,
            "Persistent layer lost across the cycle — missing: " + string.Join(", ", missing));

        // B7b — steady state: another deploy would change nothing structural.
        var pSys = await harness.RunLzAsync(
            "previewsystem --fail-on-replace", TimeSpan.FromMinutes(20), "preview-system");
        Assert.True(pSys.Ok, "previewsystem --fail-on-replace reports pending replaces/deletes " +
                             "— redeployed foundation is not steady-state.");
        var pTen = await harness.RunLzAsync(
            $"previewtenant --tenantkey {TenantKey} --fail-on-replace",
            TimeSpan.FromMinutes(20), "preview-tenant");
        Assert.True(pTen.Ok, "previewtenant --fail-on-replace reports pending replaces/deletes " +
                             "— redeployed tenant is not steady-state.");

        // B7c — HTTP smoke against the tenant root domain.
        await HttpSmokeAsync(harness);

        return after;
    }

    private static async Task HttpSmokeAsync(LifecycleHarness harness)
    {
        // systemconfig.{sk}.dev.yaml → sk; tenant config loaded anchored on the
        // repo root (no CurrentDirectory dependence).
        var sk = Path.GetFileName(harness.SystemConfigFile("dev")).Split('.')[1];
        var tenantConfig = ConfigLoader.DiscoverAndLoadTenantConfig(
            sk, TenantKey, "dev", harness.RepoRoot);
        var rootDomain = tenantConfig.RootDomain;

        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
        {
            Timeout = TimeSpan.FromSeconds(60),
        };

        // A just-recreated distribution + DNS aliases can take minutes to
        // propagate — retry the whole smoke for up to 10 minutes before
        // declaring the redeploy unhealthy.
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(10);
        Exception? lastError = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                // Apex serves something (front-door gate may 302 → /explore/home/ — fine).
                var apex = await http.GetAsync($"https://{rootDomain}/");
                Assert.True(apex.IsSuccessStatusCode,
                    $"apex https://{rootDomain}/ returned {(int)apex.StatusCode} after redeploy.");

                // /config advertises both Cognito pools (the CFAuthConfig edge
                // function reads the KVS the redeploy rebuilt).
                var config = await http.GetStringAsync($"https://{rootDomain}/config");
                Assert.Contains("tenantauth", config);
                Assert.Contains("consumerauth", config);

                Console.WriteLine($"[lifecycle] HTTP smoke passed against https://{rootDomain}/");
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or Xunit.Sdk.XunitException)
            {
                lastError = ex;
                Console.WriteLine($"[lifecycle] HTTP smoke not ready yet ({ex.Message.Split('\n')[0]}); retrying in 30s...");
                await Task.Delay(TimeSpan.FromSeconds(30));
            }
        }
        Assert.Fail($"HTTP smoke still failing 10 min after redeploy: {lastError?.Message}");
    }
}
