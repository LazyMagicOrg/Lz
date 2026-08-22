using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Lz.Tests.Orchestration.Tests;

/// <summary>
/// Shared plumbing for the teardown–redeploy lifecycle drill: repo-root
/// discovery, the `lz` subprocess runner (with per-phase artifact logs),
/// and structured access to `lz verify --json` output.
///
/// Everything here drives the INSTALLED `lz` tool as a subprocess from the
/// repo root — cwd governs env auto-detection, plugin discovery, and the
/// package-feed walk, so the test exercises exactly what an operator runs.
/// </summary>
public sealed class LifecycleHarness
{
    /// <summary>The workspace root — where the systemconfig lives.</summary>
    public string RepoRoot { get; }

    /// <summary>
    /// The Lz repository itself. Distinct from <see cref="RepoRoot"/>: Lz used to sit directly in
    /// the workspace root and now lives under <c>repos/</c>, so anything needing Lz's own tree must
    /// locate it rather than assume a fixed offset from the workspace.
    /// </summary>
    public string LzRepoRoot { get; }

    public string ArtifactsDir { get; }

    private int _step;

    public LifecycleHarness()
    {
        RepoRoot = FindRepoRoot();
        LzRepoRoot = FindLzRepoRoot();
        ArtifactsDir = Path.Combine(
            LzRepoRoot, "Lz.Tests", "Orchestration.Tests", "artifacts",
            DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(ArtifactsDir);
    }

    /// <summary>
    /// Walk up from the test bin for <c>Lz.slnx</c>. Anchoring on the solution file makes this
    /// survive relocation of the Lz repo — the previous code joined a hardcoded "lz" onto the
    /// workspace root, which broke silently when Lz moved under <c>repos/</c>.
    /// </summary>
    public static string FindLzRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (dir.GetFiles("Lz.slnx").Length > 0) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            $"Could not locate the Lz repo root (no Lz.slnx walking up from {AppContext.BaseDirectory}).");
    }

    /// <summary>
    /// Walk up from the test bin until a systemconfig.{sk}.{env}.yaml appears —
    /// the house pattern for locating the orchestration-repo root from a test
    /// (cf. ChatModuleTestFixture, DocumentRepoFixture).
    /// </summary>
    public static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (dir.GetFiles("systemconfig.*.yaml").Any(f => f.Name.Split('.').Length == 4))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            "Could not locate the repo root (no systemconfig.{sk}.{env}.yaml " +
            $"found walking up from {AppContext.BaseDirectory}).");
    }

    /// <summary>The systemconfig file for the given environment (exactly one expected).</summary>
    public string SystemConfigFile(string env)
    {
        var matches = Directory.GetFiles(RepoRoot, $"systemconfig.*.{env}.yaml");
        return matches.Length == 1
            ? matches[0]
            : throw new InvalidOperationException(
                $"Expected exactly one systemconfig.*.{env}.yaml in {RepoRoot}, found {matches.Length}.");
    }

    // =====================================================================
    // lz subprocess runner
    // =====================================================================

    public sealed record LzResult(int ExitCode, string StdOut, string StdErr, TimeSpan Elapsed)
    {
        public bool Ok => ExitCode == 0;
    }

    /// <summary>
    /// Run `lz {args}` from the repo root, tee output to an artifact file,
    /// and enforce a timeout. Never throws on non-zero exit — callers assert.
    /// </summary>
    public async Task<LzResult> RunLzAsync(string args, TimeSpan timeout, string? label = null)
    {
        var step = Interlocked.Increment(ref _step);
        label ??= args.Split(' ')[0];
        var logPath = Path.Combine(ArtifactsDir, $"{step:00}-{label}.log");
        var sw = Stopwatch.StartNew();

        var psi = new ProcessStartInfo
        {
            FileName = "lz",
            Arguments = args,
            WorkingDirectory = RepoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        // StringBuilder is not thread-safe and the DataReceived events fire on
        // pool threads — serialize every append/read on one lock.
        var ioLock = new object();
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        using var proc = new Process { StartInfo = psi };
        proc.OutputDataReceived += (_, e) =>
            { if (e.Data != null) lock (ioLock) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) =>
            { if (e.Data != null) lock (ioLock) stderr.AppendLine(e.Data); };

        Console.WriteLine($"[lifecycle] step {step}: lz {args}");
        try
        {
            proc.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // lz not installed / not on PATH — a precondition problem, not a
            // drill failure. Surface as a non-zero result so B2 can skip.
            await File.WriteAllTextAsync(logPath, $"$ lz {args}\nFAILED TO START: {ex.Message}");
            return new LzResult(-2, "", $"lz failed to start: {ex.Message}", sw.Elapsed);
        }
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        string Snapshot(StringBuilder sb) { lock (ioLock) return sb.ToString(); }

        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await proc.WaitForExitAsync(cts.Token);
            // WaitForExitAsync can return before the async readers drain; the
            // synchronous overload blocks until both streams hit EOF.
            proc.WaitForExit();
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            // Flush the readers for whatever output made it out before the kill.
            try { proc.WaitForExit(10_000); } catch { /* best effort */ }
            await File.WriteAllTextAsync(logPath,
                $"$ lz {args}\nTIMED OUT after {timeout}.\n\n{Snapshot(stdout)}\n--- stderr ---\n{Snapshot(stderr)}");
            return new LzResult(-1, Snapshot(stdout),
                $"TIMED OUT after {timeout}. {Snapshot(stderr)}", sw.Elapsed);
        }

        sw.Stop();
        await File.WriteAllTextAsync(logPath,
            $"$ lz {args}\nexit={proc.ExitCode} elapsed={sw.Elapsed}\n\n{Snapshot(stdout)}\n--- stderr ---\n{Snapshot(stderr)}");
        Console.WriteLine($"[lifecycle] step {step}: exit={proc.ExitCode} elapsed={sw.Elapsed:hh\\:mm\\:ss} → {Path.GetFileName(logPath)}");
        return new LzResult(proc.ExitCode, Snapshot(stdout), Snapshot(stderr), sw.Elapsed);
    }

    // =====================================================================
    // lz verify --json access
    // =====================================================================

    public sealed record VerifySnapshot(
        JsonElement Document,
        int StackPresent, int StackAbsent, int StackTombstoned,
        int PersistentPresent, int PersistentAbsent,
        int SmokePassed, int SmokeFailed, bool? ExpectMet, int Errors)
    {
        // Errored checks make any classification unreliable — a snapshot with
        // errors is neither cleanly deployed nor cleanly destroyed (callers
        // additionally gate on Errors before drilling).
        public bool LooksDeployed =>
            Errors == 0 && StackAbsent == 0 && StackTombstoned == 0 && StackPresent > 0;
        public bool LooksDestroyed =>
            Errors == 0 && StackPresent == 0 && StackTombstoned == 0;
        public bool LooksPartial => !LooksDeployed && !LooksDestroyed;

        /// <summary>Names of persistent resources currently Present (baseline diffing).</summary>
        public IReadOnlySet<string> PresentPersistentNames()
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in Document.GetProperty("results").EnumerateArray())
                if (r.GetProperty("category").GetString() == "persistent"
                    && r.GetProperty("state").GetString() == "Present")
                    names.Add($"{r.GetProperty("service").GetString()}:{r.GetProperty("name").GetString()}");
            return names;
        }
    }

    /// <summary>Run `lz verify --json` and parse the snapshot. Throws on malformed output.</summary>
    public async Task<VerifySnapshot> VerifyAsync(string extraArgs = "")
    {
        var result = await RunLzAsync($"verify --json {extraArgs}".TrimEnd(),
            TimeSpan.FromMinutes(10), "verify");
        if (!result.Ok && string.IsNullOrWhiteSpace(result.StdOut))
            throw new InvalidOperationException(
                $"lz verify failed (exit {result.ExitCode}): {result.StdErr}");

        // Tolerate stdout noise around the document (plugin-loader banners,
        // future prints): slice from the first '{' to the last '}'. A repo with
        // multiple systemconfigs would emit multiple documents — that is a
        // configuration this harness does not support; Parse throws on the
        // concatenation, which is the right loud failure.
        var raw = result.StdOut;
        var first = raw.IndexOf('{');
        var last = raw.LastIndexOf('}');
        if (first < 0 || last <= first)
            throw new InvalidOperationException(
                $"lz verify --json produced no JSON document (exit {result.ExitCode}). " +
                $"stdout: {raw[..Math.Min(raw.Length, 500)]}");
        var doc = JsonDocument.Parse(raw[first..(last + 1)]).RootElement;
        var s = doc.GetProperty("summary");
        // expectMet is null when no --expect was passed (JSON null → nullable).
        bool? expectMet = doc.TryGetProperty("expectMet", out var em)
            && em.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? em.GetBoolean() : null;
        return new VerifySnapshot(
            doc,
            s.GetProperty("stackPresent").GetInt32(),
            s.GetProperty("stackAbsent").GetInt32(),
            s.GetProperty("stackTombstoned").GetInt32(),
            s.GetProperty("persistentPresent").GetInt32(),
            s.GetProperty("persistentAbsent").GetInt32(),
            s.GetProperty("smokePassed").GetInt32(),
            s.GetProperty("smokeFailed").GetInt32(),
            expectMet,
            s.GetProperty("errors").GetInt32());
    }
}
