# Ticketing AI Assistant -- agent (.NET host with embedded Python)
#
# Two things make this image different from an ordinary ASP.NET publish, and
# both are easy to get wrong:
#
#  1. The interpreter is embedded, not a child process. pythonnet dlopen()s
#     CPython's shared library, so the image needs the *shared* build
#     (libpython3.x.so), not just a python binary. A distro package often ships
#     only the static or unversioned form.
#
#  2. The Python environment is built inside this image. A virtualenv is not
#     relocatable -- pyvenv.cfg holds an absolute `home` -- so the Windows venv
#     used on IIS cannot be copied here, and neither could a venv built on a
#     different machine. Installing into the image's own interpreter is the
#     only portable option, which is why Python:Venv points at /usr/local.
#
# Python 3.14 is required: langgraph and pythonnet 3.2.1 are pinned against it,
# and the notebook and smoke tests are run with that interpreter.

# ---------------------------------------------------------------- build stage
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Restore first, as its own layer. Copying only the project files means a
# source-only edit does not invalidate the (slow) NuGet restore.
COPY src/OpsLookup.NetCorePythonNetApp/AI.TicketingAssistant.PythonNetApp.csproj \
     src/OpsLookup.NetCorePythonNetApp/
RUN dotnet restore src/OpsLookup.NetCorePythonNetApp/AI.TicketingAssistant.PythonNetApp.csproj

# The Python tree is Content, so it has to be present for the publish to pick
# it up. .venv and smoke_test.py are excluded by the csproj; that exclusion is
# load-bearing -- .env holds the LongCat key and MSBuild globs the filesystem,
# not .gitignore.
COPY src/OpsLookup.NetCorePythonNetApp/ src/OpsLookup.NetCorePythonNetApp/

# No -p:IncludePythonVenv=true. The Windows venv in the project would be
# copied and is useless here: it contains .exe launchers and Windows DLLs.
RUN dotnet publish src/OpsLookup.NetCorePythonNetApp/AI.TicketingAssistant.PythonNetApp.csproj \
        -c Release -o /app/publish --no-restore

# -------------------------------------------------------------- runtime stage
# Starts from python, not from the aspnet image, so the interpreter and its
# shared library are guaranteed present and version-matched. The .NET runtime
# is installed into it afterwards.
FROM python:3.14-slim-bookworm AS runtime

# ca-certificates for the LongCat endpoint; curl for the healthcheck below.
#
# libicu is required and must not be skipped. .NET hard-fails at startup
# without it ("Couldn't find a valid ICU package"), and the usual workaround of
# setting InvariantGlobalization=true is wrong for this application: it is
# Arabic-first, and invariant mode changes string comparison, casing and
# culture formatting behaviour -- the prompt text and Arabic replies are exactly
# where that shows up. Installing libicu-dev rather than a versioned libicuNN
# keeps this working when the base image moves to a newer Debian.
RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates curl libicu-dev \
 && rm -rf /var/lib/apt/lists/*

# ASP.NET Core 9 runtime, matching the target framework. DotnetEnableTelemetry
# is suppressed so a container start does not spend time on first-run
# experience.
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

RUN curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh \
 && bash /tmp/dotnet-install.sh --channel 9.0 --runtime aspnetcore --install-dir /usr/share/dotnet \
 && rm /tmp/dotnet-install.sh \
 && ln -s /usr/share/dotnet/dotnet /usr/local/bin/dotnet

# pythonnet dlopen()s libpython3.x.so by name. The official python image is
# built --enable-shared, but the unversioned symlink is not guaranteed to be
# present, and it is the name that gets resolved. Created here if missing
# rather than assuming it, because the failure otherwise appears as an opaque
# "Failed to load Python shared library" at startup.
RUN set -eux; \
    lib="$(ls -1 /usr/local/lib/libpython3*.so.1.0 2>/dev/null | head -n1)"; \
    test -n "$lib"; \
    target="/usr/local/lib/$(basename "${lib%.so.1.0}").so"; \
    if [ ! -e "$target" ]; then ln -s "$lib" "$target"; fi; \
    ldconfig; \
    python3 -c "import sysconfig; print('shared lib:', sysconfig.get_config_var('LDLIBRARY'))"

WORKDIR /app
COPY --from=build /app/publish .

# Python dependencies go into the image's own interpreter, which is what
# PythonEngine.PythonHome points at. numpy, pydantic-core and grpcio ship
# wheels for this platform, so no compiler is needed.
COPY src/OpsLookup.NetCorePythonNetApp/Python/requirements.txt /tmp/requirements.txt
RUN pip install --no-cache-dir --upgrade pip \
 && pip install --no-cache-dir -r /tmp/requirements.txt \
 && rm /tmp/requirements.txt \
 && python3 -c "import langgraph, httpx, dotenv; print('python deps ok')"

# Non-root. The app pool identity under IIS had no write access to the
# application folder either, which is why runtime state lives on a volume.
RUN groupadd --system --gid 1001 ticketing \
 && useradd  --system --uid 1001 --gid ticketing --no-create-home ticketing \
 && mkdir -p /var/lib/ticketing \
 && chown -R ticketing:ticketing /var/lib/ticketing

USER ticketing

# Where logs/ and checkpoints.sqlite go. Mount a volume here or that history is
# lost with the container.
ENV OPSLOOKUP_DATA_DIR=/var/lib/ticketing

# Kestrel serves directly; there is no IIS and no ANCM in a container.
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://+:8080 \
    DOTNET_gcServer=1

# The embedded interpreter. /usr/local is the image's own install, so
# pyvenv.cfg is not involved and ResolveBasePythonDir falls through to it.
ENV Python__Venv=/usr/local

EXPOSE 8080

# /api/health reports pythonReady and apiReachable, which is the thing worth
# gating on -- a container that is up but whose interpreter failed to load is
# not serving anything.
HEALTHCHECK --interval=30s --timeout=10s --start-period=180s --retries=5 \
    CMD curl -fsS http://localhost:8080/api/health | grep -q '"pythonReady":true' || exit 1

ENTRYPOINT ["dotnet", "AI.TicketingAssistant.PythonNetApp.dll"]