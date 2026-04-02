# Lz — Deployment Framework

A .NET global tool for deploying multi-tenant SaaS platforms to AWS using Pulumi. Systems define their topology via a plugin (`Deploy/`), and `lz` handles infrastructure provisioning, database initialization, and service deployment.

See `Design.md`, `Requirements.md`, and `Implementation.md` for architecture details.

## Project Structure

| Project | Purpose |
|---------|---------|
| `Lz.Core` | Config, interfaces, orchestration — cloud-agnostic |
| `Lz.Aws` | AWS Pulumi components, Lambda handlers, ECS/RDS/EFS |
| `Lz.Azure` | Azure Pulumi components (placeholder) |
| `Lz.Cli` | `dotnet tool` entry point, plugin discovery, command routing |

## Version Management

All packages share a single version defined in `LzVersion.props`:

```xml
<LzVersion>0.9.109</LzVersion>
```

**All three packages (Lz.Core, Lz.Aws, Lz.Cli) must be at the same version.** A mismatch causes `TypeLoadException` at runtime.

## Building, Packing, and Installing

There is no `.sln` file. The `SolutionDir` MSBuild property must be passed explicitly so that `LzVersion.props` can be resolved.

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

**After installing the tool**, run `lz deployfoundation` to push the updated Lambda to AWS. The Lambda is a foundation-level resource — `lz deploytenant` does not update it.

> **Warning:** Never use `aws lambda update-function-code` or any direct AWS CLI/Console commands to modify resources managed by Pulumi. This causes Pulumi state drift — Pulumi thinks the resource matches its state and skips updates on subsequent deployments, leaving broken resources in AWS. Always use `lz deploy*` commands.

## Common Commands (from a system repo)

```bash
# Deploy foundation (VPC, ECS cluster, RDS, EFS, Lambda)
lz deployfoundation

# Deploy a tenant (infra + DB init + services)
lz deploytenant
lz deploytenant --tenantkey meadows

# Deploy shared services (Keycloak + Tailscale)
lz deployshared

# Check status
lz status
```

Requires the `Deploy/` plugin in the current repo (discovered by convention).
