# Future Issues

Known issues and enhancements identified during reviews or deployment testing
that have been deliberately deferred. Each entry explains the observed symptom
(if any), why it was deferred, and a suggested path forward. Items here are
candidates for future work — not a roadmap.

Add an entry whenever you find something worth addressing later but out of
scope for the current task. Remove entries once they're fixed. Keep each entry
self-contained so a future contributor can act on it cold.

---

## Secret handling inconsistency across the four Secret sites

**Symptom.** A `lz destroytenant` followed by `lz deploytenant` for the same
tenant fails with
`InvalidRequestException: secret ... is already scheduled for deletion`.
Fixed in 0.9.241 for the one failing site; the pattern is inconsistent across
the codebase.

**Context.** Four Pulumi resources create Secrets Manager secrets:

| File | Strategy today |
|------|----------------|
| `Lz.Aws/Data/AwsTenantDataComponent.cs` (ex-AppRunner) | `RecoveryWindowInDays = 0` on non-prod (fixed 0.9.241) |
| `Lz.Aws/Data/AwsRdsComponent.cs` | `RetainOnDelete = true` |
| `Lz.Aws/Ops/AwsSesComponent.cs` | `RetainOnDelete = true` |
| `Lz.Aws/Data/AwsFargateAlbTenantDataComponent.cs` | `RetainOnDelete = true` |

`RetainOnDelete = true` keeps the AWS secret alive when Pulumi destroys the
stack, which is fine for "stack-gone, secret-preserved" semantics — but on
the next `lz deploy*`, Pulumi tries to `CreateSecret` with the same name and
hits `AlreadyExists`. The `RetainOnDelete` sites would exhibit the same
class of problem (different signature) if anyone destroyed and redeployed
them; no one has hit it in practice because the topologies using those
sites (ecs-fargate-keycloak) aren't regularly destroyed.

**Suggested fix.** Normalize to the `RecoveryWindowInDays = 0` pattern for
non-prod across all four sites:

```csharp
RecoveryWindowInDays = env is "prod" or "staging" ? 30 : 0,
```

Drop `RetainOnDelete = true` on those same non-prod paths. Keep
`Protect = true` on prod so destroys require explicit operator action.

**Why deferred.** Only the (ex-AppRunner) tenant secret was causing a live
blocker. The other three work fine today because they're not exercised in a
destroy/redeploy loop.

---

## IAM policy scope — Bedrock foundation-model ARNs

**Symptom.** None. The Bedrock statement in the tenant task policy is
`Resource: "*"`. See
[AwsFargateTenantServiceComponent.cs](Lz.Aws/Compute/Fargate/AwsFargateTenantServiceComponent.cs) and
[AwsFargateServiceComponent.cs](Lz.Aws/Compute/Fargate/AwsFargateServiceComponent.cs)
(the third site, the apprunner-topology tenant service, was deleted in 0.11.0).

**Context.** The deep-pass security audit flagged these. Cognito and
CloudFront scoping landed in 0.9.237; Bedrock stayed broad because
foundation-model ARNs are cross-region and not known at policy-construction
time (the task might call models in any enabled region, and
`bedrock:InvokeModel` needs the full model ARN to scope).

**Suggested fix.** Either:
- Add a `BedrockModels` list on `AwsSystemConfig` (or topology descriptor)
  and scope the policy to the explicit ARNs. Requires deciding the model
  set per deploy.
- Add an account-scope-but-resource-wildcard constraint with a model-id
  `Condition` block, once AWS supports that (check IAM condition keys).

**Why deferred.** Requires a policy decision from the system owner about
which models are allowed. Leaving at `*` is the current deliberate default.

---

## IAM policy scope — RDS KMS key policy

**Symptom.** None. KMS key policy for RDS storage encryption grants
`kms:*` to the account root.

**Context.** Standard AWS pattern, but the deep-pass audit flagged
it as overly broad. See `Lz.Aws/Ecs/AwsRdsComponent.cs` around the
`KmsKey` + `KeyPolicy` block.

**Suggested fix.** Enumerate only the actions RDS needs
(`kms:Decrypt`, `kms:DescribeKey`, `kms:GenerateDataKey`,
`kms:GenerateDataKeyWithoutPlaintext`, `kms:ReEncrypt*`, `kms:CreateGrant`)
instead of `*`.

**Why deferred.** The `kms:*` to root is AWS's *recommended* starting
policy — every KMS key needs at least admin permissions for the root
principal to avoid lockout. Narrowing requires care to keep
CloudTrail-visible administration functional.

---

## Lambda gate-checker password handling

**Symptom.** None.
`Lz.Aws/Lambda/GateChecker/handler.py` writes DB connection strings
containing plaintext passwords to EFS-backed config files (readable by app
UID 1000). It also passes the master password via `PGPASSWORD` env var,
which is visible in process listings.

**Context.** Flagged in the security audit. This is BCProjNew-specific
(the Lambda is system-neutral code but only exercised on the
`ecs-fargate-keycloak` topology).

**Suggested fix.**
- Switch to AWS RDS IAM auth (short-lived tokens instead of passwords), or
- Use `.pgpass` (mode 0600) or stdin for `psql`, avoid env-var password
  exposure.
- Stop writing plaintext DB creds to EFS; have the app fetch from Secrets
  Manager at startup.

**Why deferred.** Requires coordinated changes across the Lambda, ECS
tenant services, and the application code that reads the config file.

---

## Build / packaging hygiene

**Symptom.** None. `dotnet build` warns `NU5104: A stable release of a
package should not have a prerelease dependency` because `Lz.Core` and
`Lz.Cli` reference `System.CommandLine [2.0.0-beta4.22272.1, )`.

**Context.** Deep-pass build audit noted several hygiene items. No
production impact.

**Items.**
- **System.CommandLine beta dependency** — no stable 2.x yet; consider
  `NoWarn=NU5104` with a comment, or migrate to `System.CommandLine`
  2.0 when it ships.
- **Missing package metadata** — `Authors`, `Copyright`, `RepositoryUrl`,
  `PackageReadmeFile` absent on published packages. Add to a new
  `Directory.Build.props` or `CommonPackageHandling.targets`.
- **`Lz.Gen` has `Nullable=disable`** while other projects use `enable`.
  Align once; it's a codegen library ported from LazyMagicMDD and its
  types flow into consumer code.
- **`Lz.Core` references Pulumi** (`Pulumi.Automation`) — the "platform-
  neutral" framing is aspirational given this dependency. Either rename
  the layer or formally accept Pulumi as the default orchestrator in
  docs.
- **No `Directory.Build.props`** centralizing `Nullable`, `LangVersion`,
  `TreatWarningsAsErrors`. Easy to add.

**Why deferred.** Housekeeping; none block functionality.

---

## Error-handling consistency pass

**Symptom.** Minor. Inconsistent patterns:
- `Lz.Cli/Program.cs` mixes `return 1` and `Environment.ExitCode = 1`.
- Most validation paths throw `InvalidOperationException`; a specific
  `ValidationException` would scan better.
- Some error output goes to stdout (`Console.WriteLine`) rather than
  stderr (`Console.Error.WriteLine`) — matters for shell scripting.

**Suggested fix.** Single pass standardizing:
- Exit codes: `0` success, `1` generic failure, `2` config/validation
  failure, `130` cancellation (SIGINT).
- All errors via a helper (`LogError(string)`) that uses
  `Console.Error.WriteLine` and applies a red `[ERROR]` prefix.
- Consider a domain `ValidationException` for config/topology errors.

**Why deferred.** Stylistic; low risk; no one has reported misclassified
exit codes breaking a script.

---

## Plugin-initialization failure surfaces as cryptic error downstream

**Symptom.** If `ILzPlugin.RegisterTopologies` throws at startup, the CLI
logs a warning and continues. A later `lz deploysystem` then fails with
`Unknown AWS topology '...'` because the plugin topology was never
registered — the user doesn't see a connection between the two.

**Context.** Noted in the deep-pass error-handling review. See
`Lz.Cli/Program.cs:58-77`.

**Suggested fix.** Either fail fast when plugin initialization throws, or
cache the original plugin-init error and include it in the
`Unknown topology` message as a likely root cause.

**Why deferred.** No reported occurrence in practice.

---

## ~~Topology coupling — Express reuses AppRunner components~~ (RESOLVED 0.11.0)

**Resolved by the 0.11.0 axis restructure**
([Migrations/AxisRestructure.md](Migrations/AxisRestructure.md)): the
apprunner topology was retired and the shared components were moved to
capability folders and renamed by capability —
`AwsDynamoDbComponent` (Data/), `AwsS3FileStorageComponent` (Storage/),
`AwsCognitoComponent` (Auth/), `AwsTenantDataComponent` (Data/), and
`AwsFargateServiceComponent` (Compute/Fargate/). No component carries a
topology name any more, so there is no "AppRunner-only change" to make.

---

## ~~Orphaned cross-topology action wiring~~ (RESOLVED 0.11.0)

**Resolved by the 0.11.0 axis restructure**: the apprunner platform factory
was deleted. The system-table-ensure post-deploy action it shared is now
`Topologies/AwsEcsFargateCognitoDynamodbFoundationPostDeployAction`,
instantiated by the two Cognito-topology factories that legitimately share
it (Fargate + Lambda), which is pinned by
`SystemPostDeployActionTests`.

---

## Runtime ownership of pragmatic subtenants

**Context.** Subtenants may be created at runtime by application code
(outside `subtenantconfig.yaml`). The KVS refresh contract in
`ILzPlugin.RefreshTenantRuntimeAsync` is documented as additive-only so
it doesn't clobber programmatic entries. Open design question: what
calls the same primitives at runtime?

**Options.**
- A. Runtime service re-implements naming + S3/DynamoDB SDK calls
  (risk: drift from `lz`).
- B. Expose `Lz.Aws.Shared.SubtenantProvisioner` /
  `SubtenantBucketManager` as a stable-API NuGet package that runtime
  services depend on.
- C. Runtime shells out to `lz deploysubtenants` (heavy, but always in
  sync).

**Why deferred.** Architectural decision; needs a concrete use case to
ground it.

---

## Internal "Foundation" naming

**Context.** After the CLI rename (`deployfoundation` →
`deploysystem`), internal C# types and method names carrying "Foundation"
were deliberately kept: `DeployFoundationAsync`, `FoundationGates`,
`FoundationLayerServices`, `AwsFoundationLookup`,
`AwsFoundationPostDeployAction`, `GetFoundationPostDeployAction`, and
related XML docs.

**Suggested fix.** Rename to "System" across the codebase for
consistency with the CLI. Broader refactor; touches many files.

**Why deferred.** Current names describe a meaningful internal concept
(the "foundation layer" as distinct from tenant-layer stacks). Renaming
loses that distinction unless a replacement term is chosen.
