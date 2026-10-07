FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
# AWS Lambda Web Adapter: runs this ASP.NET host on AWS Lambda (Function URL / API Gateway) by
# proxying Lambda invocations to the app's HTTP listener on AWS_LWA_PORT. The adapter is a Lambda
# extension under /opt/extensions; it is launched only by the Lambda execution environment's
# extension loader, so on non-Lambda hosts (ECS/Fargate, EC2, local) nothing reads /opt/extensions
# and the binary stays dormant. This single image therefore works for both lambda-* and ecs-* topologies.
COPY --from=public.ecr.aws/awsguru/aws-lambda-adapter:1.0.1 /lambda-adapter /opt/extensions/lambda-adapter
# The aspnet:10.0 image already bakes ASPNETCORE_HTTP_PORTS=8080, so the app listens on 0.0.0.0:8080.
# AWS_LWA_PORT must match that port; EXPOSE documents it.
ENV AWS_LWA_PORT=8080
# Point the LWA readiness check at the real health endpoint instead of relying on the default "/" probe.
ENV AWS_LWA_READINESS_CHECK_PATH=/health
WORKDIR /app
EXPOSE 8080

# Pinned to the exact patch the repo's global.json demands. A floating sdk:10.0 resolves to
# whatever is newest, and global.json (rollForward: disable) lands in this build context
# via COPY . . - so a floating tag would fail the in-image restore the moment the registry
# moved ahead of the workstation. The runtime stage above stays floating on purpose: it
# reads no global.json.
FROM mcr.microsoft.com/dotnet/sdk:10.0.301 AS build
WORKDIR /src

ARG ContainerName=""

# WHICH LANE THE FIRST-PARTY PACKAGES COME FROM. REQUIRED, and deliberately WITHOUT A DEFAULT.
#
#   local      - /src/DockerPackages, filled by DockerPackageSyncer from a HOST restore. What
#                `lz deploycontainer` passes; the loop that works today.
#   published  - LazyMagicOrg's registry and nuget.org, and nothing on this machine.
#
# A DEFAULT WOULD DEFEAT THE POINT whichever way it fell. Defaulting to `local` lets a pipeline that
# forgets the arg sign an image whose contents depend on a workstation. Defaulting to `published`
# breaks `lz deploycontainer` the moment it is run from an older config. So neither: the build
# refuses. This mirrors DecoupledCd.md section 4.3's rule for `builtFrom.packages` - a value that is
# required with an explicit "none" beats a value that may be absent, because absence cannot be
# distinguished from a writer that forgot.
ARG PackageLane=""

# Copy entire solution. .dockerignore excludes /Packages/ - see that file for why DockerPackages is
# NOT excluded with it.
COPY . .

RUN case "${PackageLane}" in \
      published|local) echo "PackageLane=${PackageLane}" ;; \
      "") echo "REFUSED: PackageLane is required and has no default. Pass --build-arg PackageLane=published for a" >&2; \
          echo "         signable image, or =local for the DockerPackages loop. See this Dockerfile's ARG note." >&2; \
          exit 1 ;; \
      *)  echo "REFUSED: PackageLane='${PackageLane}' is not 'published' or 'local'." >&2; exit 1 ;; \
    esac

# Restore the specific container project (separate layer so package restore is cached).
#
# The config is now a COMMITTED file chosen by lane, not a chain of `echo` redirects. Two reasons
# beyond tidiness: a generated file is not reviewable in a diff, and it cannot be named by
# RestoreConfigFile from outside the image when someone wants to reproduce the restore by hand.
#
# THE CREDENTIAL IS A BUILDKIT SECRET, NEVER A BUILD ARG. An ARG is recorded in the image history,
# so `docker history` would print it. The published lane needs one even though LazyMagicOrg's
# packages are public, because GitHub Packages 401s on an anonymous read; the local lane needs none,
# which is why the mount is optional and the export is guarded by the file's existence.
WORKDIR /src/Containers/${ContainerName}
RUN --mount=type=secret,id=nuget_creds \
    if [ -f /run/secrets/nuget_creds ]; then \
      export NuGetPackageSourceCredentials_LazyMagicRegistry="$(cat /run/secrets/nuget_creds)"; \
    fi; \
    dotnet restore "${ContainerName}.csproj" \
      -p:RestoreConfigFile=/src/Containers/${ContainerName}/docker.${PackageLane}.NuGet.Config

# Publish directly (publish builds internally; a standalone `dotnet build` would be redundant since
# only /app/publish is copied into the final image). --no-restore reuses the cached restore above.
FROM build AS publish
ARG ContainerName=""
RUN dotnet publish "${ContainerName}.csproj" -c Release -o /app/publish \
    --no-restore -p:GeneratePackageOnBuild=false

FROM base AS final
ARG ContainerName=""
ARG PackageLane=""

# THE LANE TRAVELS WITH THE IMAGE. A build record's `builtFrom.lane` (DecoupledCd.md section 4.3) is
# written by the build workflow, which is outside the trust boundary; this label is the same fact
# read off the artifact, so Verify can CORROBORATE the record rather than taking its word. Deliberately
# just the lane: the resolved package versions belong in the record, sourced from project.assets.json,
# and putting a second copy in the image would create two sources of truth for one fact.
LABEL org.lazymagic.package-lane="${PackageLane}"
LABEL org.lazymagic.container-name="${ContainerName}"

ENV CONTAINER_DLL=${ContainerName}.dll
WORKDIR /app
COPY --from=publish /app/publish .
# exec-form via sh so ${CONTAINER_DLL} is expanded AND `exec` makes dotnet PID 1, so SIGTERM is
# forwarded for graceful shutdown on ECS/Fargate. On Lambda the LWA extension manages lifecycle.
ENTRYPOINT ["sh", "-c", "exec dotnet ${CONTAINER_DLL}"]
