# syntax=docker/dockerfile:1
# The game edge (S7) as a container image. Built from the repository root, because the edge
# references src/ projects:
#
#   docker build -f workloads/game-edge/Dockerfile --build-arg REVISION=$(git rev-parse HEAD) .
#
# REVISION is the commit the image is built from. The edge reports it at /health/deployed, which
# SubZeroDev.Infra's deploy waits on; CI passes github.sha. Without it the image still runs, and
# /health/deployed answers 503 with a null revision.
#
# The image carries no configuration of a deployment. Mode, workload address, handoff and issuer
# all come from the environment (workloads/game-edge/README.md, "Configuration"), and the handoff
# signing key from GAME_EDGE_HANDOFF_SIGNING_KEY_PEM_B64 (docker-entrypoint.sh).

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY Directory.Build.props ./
COPY src/ src/
COPY workloads/game-edge/SubZeroDev.Platform.GameEdge/ workloads/game-edge/SubZeroDev.Platform.GameEdge/
RUN dotnet publish workloads/game-edge/SubZeroDev.Platform.GameEdge/SubZeroDev.Platform.GameEdge.csproj \
      --configuration Release --output /app -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
ARG REVISION=""
ENV GameEdge__Revision=$REVISION \
    ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://+:8080 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1
# appsettings.json resolves against the working directory, and the two timeouts' defaults live
# only there (README, "Run it locally"), so the edge runs from its own directory.
WORKDIR /app
COPY --from=build /app ./
COPY --chmod=0755 workloads/game-edge/docker-entrypoint.sh /usr/local/bin/game-edge-entrypoint
USER app
EXPOSE 8080
ENTRYPOINT ["/usr/local/bin/game-edge-entrypoint"]
