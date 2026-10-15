# Data API. Plain .NET -- no interpreter, no virtualenv, nothing machine-specific
# in the image.
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY src/OpsLookup.Api/AI.TicketingAssistant.Api.csproj src/OpsLookup.Api/
RUN dotnet restore src/OpsLookup.Api/AI.TicketingAssistant.Api.csproj

COPY src/OpsLookup.Api/ src/OpsLookup.Api/
RUN dotnet publish src/OpsLookup.Api/AI.TicketingAssistant.Api.csproj \
        -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

# libicu is required; .NET hard-fails at startup without it. Invariant
# globalization is not the fix for an Arabic-first application, because it
# changes string comparison and casing.
RUN apt-get update \
 && apt-get install -y --no-install-recommends libicu-dev curl \
 && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

# Non-root. Migrations are not run here: they are applied by deploy.ps1 against
# the database, because the runtime account is granted only db_datareader and
# db_datawriter and cannot alter the schema.
RUN groupadd --system --gid 1001 opslookup \
 && useradd  --system --uid 1001 --gid opslookup --no-create-home opslookup \
 && chown -R opslookup:opslookup /app
USER opslookup

# The scheme must be http://+, not http+://. Kestrel parses the value before
# anything else, so a malformed one exits with 139 during startup and the only
# trace is a BindingAddress.Parse stack.
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://+:8080

EXPOSE 8080

HEALTHCHECK --interval=20s --timeout=5s --start-period=20s --retries=5 \
    CMD curl -fsS http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "AI.TicketingAssistant.Api.dll"]