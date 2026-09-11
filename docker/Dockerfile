# Generic image for the runtime-only servers (mcp-filesystem, mcp-database, mcp-index, mcp-learnings).
# Build from the repository root:
#   docker build -f docker/Dockerfile --build-arg SERVER=McpServices.Index -t mcp-index .
# mcp-roslyn needs the SDK at runtime; use docker/Dockerfile.roslyn for it.
ARG DOTNET_VERSION=10.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
ARG SERVER=McpServices.Index
WORKDIR /src
COPY global.json Directory.Build.props Directory.Build.targets Directory.Packages.props McpServices.slnx ./
COPY src ./src
RUN dotnet restore src/servers/${SERVER}/${SERVER}.csproj \
 && dotnet publish src/servers/${SERVER}/${SERVER}.csproj --configuration Release --no-restore --no-self-contained --output /app

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS runtime
ARG SERVER=McpServices.Index
# git: mcp-index uses it for freshness tracking; ca-certificates: mcp-learnings talks to the Cursor API.
RUN apt-get update \
 && apt-get install -y --no-install-recommends git ca-certificates \
 && rm -rf /var/lib/apt/lists/* \
 && useradd --create-home --uid 10001 mcp
WORKDIR /app
COPY --from=build --chown=mcp:mcp /app ./
ENV SERVER_DLL=${SERVER}.dll \
    MCP_SERVICES_HOME=/data \
    MCP_HOST=0.0.0.0 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1
RUN mkdir -p /data && chown mcp:mcp /data
VOLUME ["/data"]
USER mcp
EXPOSE 5100
# Default: Streamable HTTP on 0.0.0.0:5100 (requires MCP_AUTH_TOKEN). Override the command to run over stdio
# (e.g. `docker run -i ... mcp-index --root /workspace`).
ENTRYPOINT ["/bin/sh", "-c", "exec dotnet /app/${SERVER_DLL} \"$@\"", "--"]
CMD ["--http", "--port", "5100"]
