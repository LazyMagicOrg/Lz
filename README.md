# Lz — Deployment Framework

A .NET global tool for deploying multi-tenant SaaS platforms to AWS using Pulumi. Systems define their topology via a plugin (`Deploy/`), and `lz` handles infrastructure provisioning, database initialization, and service deployment.

See `Design.md`, `Requirements.md`, and `Implementation.md` for architecture
details, and `FutureIssues.md` for known deferred improvements.

## Project Structure

| Project | Purpose |
|---------|---------|
| `Lz.Core` | Platform-neutral config, interfaces, orchestration scaffolding. Speaks in shapes only — no AWS/Azure vocabulary. |
| `Lz.Aws` | AWS-specific config types (AwsSystemConfig/AwsTenantConfig/AwsSharedConfig), derived via `IConfigExtensions` YAML type mappings. Pulumi components, the ecs-fargate-keycloak / ecs-fargate-cognito-dynamodb / lambda-cognito-dynamodb topologies (axis-structured since 0.11.0: Compute/Auth/Data/Storage/Edge/Ops + Topologies/), Cognito/Keycloak/Tailscale/ACM implementations, AWS-shaped orchestration. |
| `Lz.Azure` | Stub `IPlatformFactory` placeholder. |
| `Lz.Cli` | `dotnet tool` entry point, plugin discovery, command routing. |
| `Lz.Runner` | Thin dispatcher that resolves the correct `Lz.Cli` nupkg from NuGet feeds walked up from the current directory. |
| `Lz.Gen` | Model-driven code generation (ported from LazyMagicMDD). |

Systems extend the tool via a **plugin** (`Deploy/` project) that implements
`ILzPlugin`. Plugins are allowed to be platform-aware — see
`BCProjNew/Deploy/` for a worked AWS example. Plugin authors cast the
base config types via `config.Aws()` to read AWS-derived fields.

For design details see [`Design.md`](Design.md), and the target-isolation
architecture doc at [`Design/TargetIsolation.md`](Design/TargetIsolation.md).

## Version Management

All packages share a single version defined in `LzVersion.props`.
**All packages must be at the same version** — a mismatch causes
`TypeLoadException` at runtime.

## Building, Packing, and Installing

Build through **`Lz.slnx`**, which sets `SolutionDir` for you — the `Lz.*` projects need it to
resolve `LzVersion.props` / `CommonPackageHandling.targets`, and there is no `Directory.Build.props`
to supply it, so a per-project build fails with `MSB4019`.

### Prerequisite: a LazyMagic package source

`ProjectTemplates/` is compiled as a member of `Lz.slnx` (that is what catches template
contamination at build time), and those templates reference `LazyMagic.*` packages that are **not
published to nuget.org**. NuGet needs somewhere to get them.

Declare that source **above this repo** — in your user-level `%APPDATA%\NuGet\NuGet.Config`, or any
ancestor directory — pointing at your local LazyMagic package output:

```xml
<add key="LazyMagic" value="...\LazyMagic\Packages" />
```

**Do not add a `NuGet.Config` inside this repo for it.** A consuming system already declares that
same feed in its own root config, and a config *inside* Lz is a **descendant** of that root: it
escapes the root's `<clear/>`, and because NuGet deduplicates package sources by resolved path, it
silently renames the system's source out from under its `packageSourceMapping` — which then matches
nothing and fails `NU1100` for packages sitting on disk. This repo carried exactly such a file
(`ProjectTemplates/NuGet.Config`, aliasing the feed as `LazyMagicLocal`); it was removed for that
reason. Machine-specific paths belong in machine-specific config.

Note also that a fresh clone has no `Packages/` folder while `NuGet.Config` declares `Lz` →
`./Packages`. NuGet raises `NU1301` for a local source that does not exist, so create it once:
`mkdir Packages`.

### Full build and install sequence

```powershell
cd C:\Users\TimothyMay\repos\_lz

# 1. Bump version in LzVersion.props

# 2. Build all projects (Release configuration)
dotnet build /p:SolutionDir='C:\Users\TimothyMay\repos\_lz\' -c Release

# 3. Pack Aws and Cli (Core is packed during build automatically)
dotnet pack Lz.Aws\Lz.Aws.csproj -c Release -o ./nupkg /p:SolutionDir='C:\Users\TimothyMay\repos\_lz\' --no-build
dotnet pack Lz.Cli\Lz.Cli.csproj -c Release -o ./nupkg /p:SolutionDir='C:\Users\TimothyMay\repos\_lz\' --no-build

# 4. Uninstall old version and install new
dotnet tool uninstall --global Lz.Cli
dotnet tool install --global Lz.Cli --add-source .\nupkg --version <NEW_VERSION>
```

### When the gate-checker Lambda changes

The gate-checker Lambda (`Lz.Aws/Lambda/GateChecker/handler.py`) is bundled as a zip inside the `Lz.Aws` NuGet package. If you modify `handler.py` or `requirements.txt`, you must **rebuild the Lambda zip before packing**:

```powershell
cd Lz.Aws\Lambda\GateChecker

# Remove cached zip to force rebuild
Remove-Item build\gate-checker.zip -ErrorAction SilentlyContinue

# Install pip dependencies into staging area
pip install -r requirements.txt -t build\stage --quiet

# Copy handler + native binaries
Copy-Item handler.py build\stage\handler.py -Force
Copy-Item -Recurse bin build\stage\bin -Force
Copy-Item -Recurse lib build\stage\lib -Force

# Create zip in the build directory
Compress-Archive -Path build\stage\* -DestinationPath build\gate-checker.zip -Force
Remove-Item -Recurse build\stage

# IMPORTANT: Copy to the location that gets packaged into the nupkg
Copy-Item build\gate-checker.zip ..\gate-checker.zip -Force

cd ..\..\..
```

> **Important:** There are two zip locations:
> - `Lz.Aws/Lambda/gate-checker.zip` — this is what ships in the NuGet package (referenced by `Lz.Aws.csproj` as a Content item)
> - `Lz.Aws/Lambda/GateChecker/build/gate-checker.zip` — this is the build output
>
> You **must** copy the build output to `Lz.Aws/Lambda/gate-checker.zip` before packing. If you skip this step, the old Lambda code ships with the tool.

Then proceed with the normal build/pack/install sequence above.

**After installing the tool**, run `lz deploysystem` to push the updated Lambda to AWS. The Lambda is a foundation-level resource — `lz deploytenant` does not update it.

> **Warning:** Never use `aws lambda update-function-code` or any direct AWS CLI/Console commands to modify resources managed by Pulumi. This causes Pulumi state drift — Pulumi thinks the resource matches its state and skips updates on subsequent deployments, leaving broken resources in AWS. Always use `lz deploy*` commands.

## Common Commands (from a system repo)

```bash
# Deploy foundation (VPC, ECS cluster, RDS, EFS, Lambda)
lz deploysystem

# Deploy a tenant (infra + DB init + services)
lz deploytenant
lz deploytenant --tenantkey meadows

# Deploy shared services (Keycloak + Tailscale)
lz deployshared

# Check status
lz status
```

Requires the `Deploy/` plugin in the current repo (discovered by convention).
