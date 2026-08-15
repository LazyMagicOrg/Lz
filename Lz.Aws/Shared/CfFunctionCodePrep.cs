using System;
using System.IO;
using System.Linq;
using System.Text;
using NUglify;
using NUglify.JavaScript;
using Lz.Aws.Auth;
using Lz.Aws.Compute.Fargate;
using Lz.Aws.Compute.FargateAlb;
using Lz.Aws.Compute.Lambda;
using Lz.Aws.Data;
using Lz.Aws.Edge;
using Lz.Aws.Ops;
using Lz.Aws.Storage;
using Lz.Aws.Tailscale;
using Lz.Aws.Topologies;
using Lz.Aws.Config;
using Lz.Aws.Interfaces;
using Lz.Aws.Interfaces.Outputs;

namespace Lz.Aws.Shared;

/// <summary>
/// Shared helper for preparing CloudFront Function JS source before upload.
/// Reads the file, performs template substitutions, minifies (safe mode —
/// whitespace + comments only, identifiers preserved), and validates against
/// the 10 KB CloudFront Functions code-size limit.
///
/// Central home so the KVS and static CloudFront components all
/// emit identical, size-checked, minified code.
/// </summary>
public static class CfFunctionCodePrep
{
    /// <summary>CloudFront Functions hard code-size limit (10 KB / 10,240 bytes).</summary>
    public const int MaxBytes = 10240;

    /// <summary>
    /// Read the JS source at <paramref name="jsPath"/>, apply the given
    /// template substitutions, minify, verify the result fits in 10 KB, and
    /// return the minified code ready to upload to CloudFront.
    /// Logs a one-line summary of before/after sizes.
    /// </summary>
    /// <param name="jsPath">Absolute path to the CloudFront function JS source file.</param>
    /// <param name="jsFileName">File name (used in log + error messages).</param>
    /// <param name="substitutions">
    /// Placeholder → value pairs applied to the raw source before minification.
    /// Typical caller passes at least ("${KvsArn}", arn).
    /// </param>
    public static string PrepareAndValidate(
        string jsPath,
        string jsFileName,
        params (string Placeholder, string Value)[] substitutions)
    {
        if (!File.Exists(jsPath))
            throw new FileNotFoundException($"CloudFront function source not found: {jsPath}", jsPath);

        var raw = File.ReadAllText(jsPath);
        if (substitutions != null)
            foreach (var (placeholder, value) in substitutions)
                raw = raw.Replace(placeholder, value);

        var rawBytes = Encoding.UTF8.GetByteCount(raw);

        // Safe minify: strip comments + collapse whitespace only. Avoid
        // AST-level transformations — they break CloudFront Functions code
        // in two ways we hit in practice:
        //   1. NUglify hoists function declarations above top-level imports,
        //      so `import cf from 'cloudfront'` ends up at the bottom of the
        //      file. cloudfront-js-2.0 rejects that.
        //   2. NUglify rewrites `if (!X) Y;` → `X || Y;`. When Y begins with
        //      a regex literal (`/\.[a-zA-Z0-9]+$/...`) after the preceding
        //      `}`, the parser reads `}/` as division and chokes.
        // Turning MinifyCode off disables both rewrites while still stripping
        // comments and collapsing whitespace. LocalRenaming stays off for the
        // same reason — identifier mangling is risky on the cloudfront-js-2.0
        // runtime.
        var settings = new CodeSettings
        {
            MinifyCode = false,
            LocalRenaming = LocalRenaming.KeepAll,
            PreserveFunctionNames = true,
            PreserveImportantComments = false,
            StripDebugStatements = false,
            RemoveUnneededCode = false,
            ReorderScopeDeclarations = false,
            TermSemicolons = true,
            OutputMode = OutputMode.SingleLine
        };

        var result = Uglify.Js(raw, settings);
        if (result.HasErrors)
            throw new InvalidOperationException(
                $"Minify errors in {jsFileName}: " +
                string.Join("; ", result.Errors.Select(e => e.Message)));

        var code = result.Code;
        var minBytes = Encoding.UTF8.GetByteCount(code);
        var savedPct = rawBytes > 0 ? 100 * (rawBytes - minBytes) / rawBytes : 0;

        Console.WriteLine(
            $"  CF function {jsFileName}: {rawBytes:N0} → {minBytes:N0} bytes " +
            $"({savedPct}% saved, limit {MaxBytes:N0})");

        if (minBytes > MaxBytes)
            throw new InvalidOperationException(
                $"CloudFront function '{jsFileName}' is {minBytes:N0} bytes after minification — " +
                $"exceeds {MaxBytes:N0} byte limit. Consider enabling identifier mangling or refactoring.");

        return code;
    }
}
