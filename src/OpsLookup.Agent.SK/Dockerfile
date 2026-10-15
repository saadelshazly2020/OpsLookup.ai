# Ticketing AI Assistant -- Semantic Kernel agent
#
# Much simpler than the Python agent's image, and that is the point. There is no
# interpreter to install, no virtualenv to build, no shared library to link, and
# no ICU to worry about: the whole thing is one self-contained .NET publish.
#
# Consequences worth knowing:
#   * no 60-120s langgraph warm-up on start, so the container is useful as soon
#     as Kestrel binds. The old cold start was the main argument for hosting as
#     a Windows Service, and it is gone here.
#   * no per-machine virtualenv. The Windows venv is not relocatable and cannot
#     be used here at all; nothing replaces it because there is no Python.

# --------------------------------------------------------------- client stage
# Built separately so the .NET SDK image stays clean of Node. The msbuild-sdk
# project runs npm itself, but that fails inside a Linux SDK image with no npm
# and reports exit code 127, which reads as "npm is broken" rather than "npm is
# not installed here" -- so the Docker build skips it with SkipClientBuild.
FROM node:22-slim AS client
WORKDIR /client
COPY src/OpsLookup.Agent.SK/ClientApp/ ./
RUN npm ci --no-audit --no-fund && npm run build

# ---------------------------------------------------------------- build stage
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Restore on its own layer, copying only the project file first, so a source
# edit does not invalidate the (slow) NuGet restore.
COPY src/OpsLookup.Agent.SK/AI.TicketingAssistant.Agent.csproj src/OpsLookup.Agent.SK/
RUN dotnet restore src/OpsLookup.Agent.SK/AI.TicketingAssistant.Agent.csproj

# The client is copied rather than built here; see the client stage above.
COPY src/OpsLookup.Agent.SK/ src/OpsLookup.Agent.SK/
COPY --from=client /wwwroot/ src/OpsLookup.Agent.SK/wwwroot/

RUN dotnet publish src/OpsLookup.Agent.SK/AI.TicketingAssistant.Agent.csproj \
        -c Release -o /app/publish --no-restore -p:SkipClientBuild=true

# -------------------------------------------------------------- runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

# libicu is required and must not be skipped: .NET hard-fails at startup without
# it ("Couldn't find a valid ICU package"). InvariantGlobalization=true is the
# usual workaround and is wrong here -- this application is Arabic-first, and
# invariant mode changes string comparison, casing and culture formatting, which
# is exactly where the prompt text and Arabic replies live.
RUN apt-get update \
 && apt-get install -y --no-install-recommends libicu-dev curl \
 && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

# Conversation history. A volume belongs here or the history dies with the
# container; the SQLite file is opened WAL, so the directory must be writable by
# the runtime user rather than just root.
RUN groupadd --system --gid 1001 ticketing \
 && useradd  --system --uid 1001 --gid ticketing --no-create-home ticketing \
 && mkdir -p /var/lib/ticketing \
 && chown -R ticketing:ticketing /var/lib/ticketing

USER ticketing

ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://+:8080 \
    Conversation__DatabasePath=/var/lib/ticketing/conversations.sqlite \
    DOTNET_gcServer=1

EXPOSE 8080

# Gate on the data API rather than on liveness. A container that is up but
# cannot reach the lookup API answers nothing, and reporting healthy there would
# hide the only failure that matters.
HEALTHCHECK --interval=30s --timeout=10s --start-period=20s --retries=5 \
    CMD curl -fsS http://localhost:8080/api/health | grep -q '"apiReachable":true' || exit 1

ENTRYPOINT ["dotnet", "AI.TicketingAssistant.Agent.dll"]