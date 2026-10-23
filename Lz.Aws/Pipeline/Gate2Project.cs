using System.Text.Json;
using Lz.Core.Config;

namespace Lz.Aws.Pipeline;

/// <summary>What Gate 2's project needs from the workspace, which the planner cannot read: the environment's hosts, from its
/// tenant and subtenant config, which the e2e and browser tiers test.</summary>
public sealed record DeployerGate2Inputs(string BaseHost, string ApexHost);

/// <summary>
/// Gate 2's CodeBuild project (DecoupledCd.md §15.5, V-E): its role and the policy that role holds, the buildspec fixed in the
/// project, and the variables every build of it gets. Each execution's own — the bundles' URLs and SHA-256s, where to upload —
/// come from Gate2Start in the build's variables.
/// </summary>
public sealed record DeployerGate2Project(
    string RoleName, string RolePolicy, string TrustPolicy, string LogGroupName, string Buildspec,
    string Image, string ComputeType, int TimeoutMinutes, IReadOnlyDictionary<string, string> Environment);

public static class Gate2ProjectPlanner
{
    public const string Image = "aws/codebuild/standard:8.0";
    public const string ComputeType = "BUILD_GENERAL1_MEDIUM";
    /// <summary>Where the staged Icecat fixture cache lives in the results bucket, which the role may read and not write.</summary>
    public const string FixturesPrefix = "fixtures/icecat/";
    public const string FixtureCacheDir = "/tmp/icecat";
    /// <summary>The secrets the tiers read: the durable test logins, and the members the party-membership test signs in as.</summary>
    public static readonly string[] Secrets = { "e2e-users", "party-members" };

    /// <summary>The e2e tier's own calls on this account's pools: the users it creates, re-keys, enables, reads and removes,
    /// and the clients it reads. E2ETestPlan's E6: every suite deletes what it creates.</summary>
    public static readonly string[] CognitoActions =
    {
        "cognito-idp:AdminCreateUser", "cognito-idp:AdminSetUserPassword", "cognito-idp:AdminGetUser", "cognito-idp:AdminEnableUser",
        "cognito-idp:AdminDeleteUser", "cognito-idp:ListUserPoolClients", "cognito-idp:DescribeUserPoolClient",
    };

    /// <param name="gate1">The image repository's Gate 1, whose run, root and SDK Gate 2 runs its test bundle with. The deployer's
    /// planner finds it, so this file reads nothing of <c>SystemConfig.Pipeline</c> itself.</param>
    public static DeployerGate2Project Plan(
        string sk, string env, string region, string accountId, string resultsBucket, DeployerGate2Inputs inputs, PipelineGate1Config gate1)
    {

        var project = $"{sk}-{env}-gate2";
        var role = $"{project}-build";
        var logGroup = $"/aws/codebuild/{project}";
        var host = $"https://{inputs.BaseHost}";

        var environment = new Dictionary<string, string>
        {
            ["GATE2_RUN"] = gate1.Run!,
            ["GATE2_ROOT"] = gate1.BuildRoot ?? IntakeGate1StartStep.DefaultRoot,
            ["GATE2_TIERS"] = Gate2Lists.Variable(),
            ["GATE2_FIXTURES"] = $"s3://{resultsBucket}/{FixturesPrefix}",
            ["SDK_VERSION"] = gate1.SdkVersion!,
            ["SDK_SHA512"] = gate1.SdkSha512!,
            // The e2e tier, as a gate: the project's role, never a profile; a test that cannot assert fails; the staged cache.
            ["E2E__Gate"] = "true",
            ["AWS__CredentialSource"] = "Ambient",
            ["E2E__BaseHost"] = inputs.BaseHost,
            ["E2E__ApexHost"] = inputs.ApexHost,
            ["E2E__UsersSecretName"] = $"{sk}/e2e-users",
            ["E2E__FixtureCacheDir"] = FixtureCacheDir,
            // The browser tier's hosts.
            ["BFF_HOST"] = host,
            ["SELLER_HOST"] = host,
        };

        var statements = new object[]
        {
            new
            {
                Sid = "ItsOwnLogs",
                Effect = "Allow",
                Action = new[] { "logs:CreateLogStream", "logs:PutLogEvents" },
                Resource = $"arn:aws:logs:{region}:{accountId}:log-group:{logGroup}:*",
            },
            new { Sid = "ReadTheStagedFixtures", Effect = "Allow", Action = new[] { "s3:GetObject" }, Resource = $"arn:aws:s3:::{resultsBucket}/{FixturesPrefix}*" },
            new
            {
                Sid = "ListTheStagedFixtures",
                Effect = "Allow",
                Action = new[] { "s3:ListBucket" },
                Resource = $"arn:aws:s3:::{resultsBucket}",
                Condition = new Dictionary<string, object> { ["StringLike"] = new Dictionary<string, object> { ["s3:prefix"] = new[] { $"{FixturesPrefix}*" } } },
            },
            new { Sid = "UploadItsResults", Effect = "Allow", Action = new[] { "s3:PutObject" }, Resource = $"arn:aws:s3:::{resultsBucket}/*" },
            // The build runs the repositories' own test code: it may not replace the cache that code is tested with.
            new { Sid = "NeverTheStagedFixtures", Effect = "Deny", Action = new[] { "s3:PutObject", "s3:DeleteObject" }, Resource = $"arn:aws:s3:::{resultsBucket}/fixtures/*" },
            new
            {
                Sid = "ReadTheTestLogins",
                Effect = "Allow",
                Action = new[] { "secretsmanager:GetSecretValue" },
                Resource = Secrets.Select(s => $"arn:aws:secretsmanager:{region}:{accountId}:secret:{sk}/{s}-*").ToArray(),
            },
            new
            {
                Sid = "TheE2eTiersCognitoCalls",
                Effect = "Allow",
                Action = CognitoActions,
                Resource = $"arn:aws:cognito-idp:{region}:{accountId}:userpool/*",
            },
        };

        return new DeployerGate2Project(
            RoleName: role,
            RolePolicy: JsonSerializer.Serialize(new { Version = "2012-10-17", Statement = statements }),
            TrustPolicy: JsonSerializer.Serialize(new
            {
                Version = "2012-10-17",
                Statement = new[] { new { Effect = "Allow", Principal = new { Service = "codebuild.amazonaws.com" }, Action = "sts:AssumeRole" } },
            }),
            LogGroupName: logGroup,
            Buildspec: Buildspec(sk),
            Image: Image,
            ComputeType: ComputeType,
            TimeoutMinutes: DeployerPlanner.Gate2BuildTimeoutSeconds / 60,
            Environment: environment);
    }

    /// <summary>
    /// GATE 2'S BUILDSPEC, fixed in the project, never from a bundle. The pinned SDK checked by SHA-512; both bundles checked by
    /// SHA-256 before they are unzipped; the browser tests' own dependencies and Chromium; the staged cache; a .runsettings
    /// fixed here that fails a skip. Then ONE TIER AT A TIME — two at once make dev answer 500 — the e2e tiers under the gate's
    /// fixed filters, each to its own TRX, then the browser @gate tests to browser.json. Neither runner's exit code decides:
    /// both exit 0 when tests skip, so the result state reads the files, and a tier that left none is incomplete. The results
    /// are uploaded in <c>finally</c>, whatever failed. A login that cannot be fetched fails the build before a test runs.
    /// Node is the image's own (unmeasured in this project until its first build).
    /// </summary>
    public static string Buildspec(string sk) => $$"""
        version: 0.2
        env:
          shell: bash
          secrets-manager:
            BFF_USER: "{{sk}}/e2e-users:TenantUser"
            BFF_PASSWORD: "{{sk}}/e2e-users:TenantPassword"
            ADMIN_USER: "{{sk}}/e2e-users:AdminUser"
            ADMIN_PASSWORD: "{{sk}}/e2e-users:AdminPassword"
            BUYER_USER: "{{sk}}/e2e-users:ConsumerUser"
            BUYER_PASSWORD: "{{sk}}/e2e-users:ConsumerPassword"
        phases:
          install:
            commands:
              - printf 'GATE2 execution %s\nGATE2 e2e bundle %s\nGATE2 browser bundle %s\n' "$GATE2_EXECUTION" "$GATE2_E2E_BUNDLE_SHA256" "$GATE2_BROWSER_BUNDLE_SHA256"
              - curl -fsSL --retry 3 -o /tmp/sdk.tgz "https://builds.dotnet.microsoft.com/dotnet/Sdk/${SDK_VERSION}/dotnet-sdk-${SDK_VERSION}-linux-x64.tar.gz"
              - echo "${SDK_SHA512}  /tmp/sdk.tgz" | sha512sum -c -
              - mkdir -p /opt/gate2-dotnet && tar -xzf /tmp/sdk.tgz -C /opt/gate2-dotnet
          pre_build:
            commands:
              - curl -fsSL --retry 3 -o /tmp/e2e.zip "$GATE2_E2E_BUNDLE_URL"
              - echo "${GATE2_E2E_BUNDLE_SHA256}  /tmp/e2e.zip" | sha256sum -c -
              - mkdir -p "$GATE2_ROOT" && unzip -q /tmp/e2e.zip -d "$GATE2_ROOT"
              - curl -fsSL --retry 3 -o /tmp/browser.zip "$GATE2_BROWSER_BUNDLE_URL"
              - echo "${GATE2_BROWSER_BUNDLE_SHA256}  /tmp/browser.zip" | sha256sum -c -
              - mkdir -p /tmp/browser && unzip -q /tmp/browser.zip -d /tmp/browser
              - cd /tmp/browser && npm ci --no-audit --no-fund && npx playwright install --with-deps chromium
              - mkdir -p "$E2E__FixtureCacheDir" && aws s3 cp --recursive "$GATE2_FIXTURES" "$E2E__FixtureCacheDir" --only-show-errors
              - printf '%s\n' '<RunSettings><xUnit><FailSkips>true</FailSkips></xUnit></RunSettings>' > /tmp/gate2.runsettings
          build:
            commands:
              - mkdir -p /tmp/gate2-results
              - cd "$GATE2_ROOT/$(dirname "$GATE2_RUN")" && printf '%s\n' "$GATE2_TIERS" | while read -r tier filter; do [ -z "$tier" ] && continue; DOTNET_ROOT=/opt/gate2-dotnet DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 /opt/gate2-dotnet/dotnet test "$(basename "$GATE2_RUN")" --filter "$filter" --settings /tmp/gate2.runsettings --logger "trx;LogFileName=$tier.trx" --results-directory /tmp/gate2-results || true; done
              - cd /tmp/browser && npx playwright test --grep @gate --forbid-only --retries=1 --reporter=json > /tmp/gate2-results/browser.json || true
            finally:
              - aws s3 cp /tmp/gate2-results/ "$GATE2_RESULTS" --recursive --only-show-errors
        """;
}
