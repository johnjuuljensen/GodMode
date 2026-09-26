# ── Build React client ──
FROM node:22-slim AS react-build
WORKDIR /app
COPY src/GodMode.Client.React/package.json src/GodMode.Client.React/package-lock.json* ./
RUN npm ci
COPY src/GodMode.Client.React/ .
RUN npm run build

# ── Build .NET server ──
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS dotnet-build
WORKDIR /src

# Copy project files for restore (layer caching)
COPY Directory.Packages.props ./
COPY src/Directory.Build.props src/
COPY src/GodMode.Server/GodMode.Server.csproj src/GodMode.Server/
COPY src/GodMode.Shared/GodMode.Shared.csproj src/GodMode.Shared/
COPY src/GodMode.ProjectFiles/GodMode.ProjectFiles.csproj src/GodMode.ProjectFiles/
RUN dotnet restore src/GodMode.Server/GodMode.Server.csproj -p:PublishReactClient=false

# Copy source and publish
COPY src/GodMode.Server/ src/GodMode.Server/
COPY src/GodMode.Shared/ src/GodMode.Shared/
COPY src/GodMode.ProjectFiles/ src/GodMode.ProjectFiles/
RUN dotnet publish src/GodMode.Server/GodMode.Server.csproj -c Release -o /app/publish -p:PublishReactClient=false

# Copy React build output into wwwroot
COPY --from=react-build /app/dist /app/publish/wwwroot/

# ── Runtime ──
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# Create non-root user (Claude CLI refuses --dangerously-skip-permissions as root)
RUN groupadd -r godmode && useradd -r -g godmode -m -d /home/godmode godmode

ENV HOME=/home/godmode
# The godmode user's own bin directory comes last: user-scope installs land there, and a session can
# write it, so nothing in it shadows a system binary. The server names its pwsh and claude outright
# (PowerShell__Executable and Claude__Executable, below) and looks up neither on PATH.
ENV PATH="${PATH}:/home/godmode/.local/bin"

# Install system deps + Node.js (for the npx MCP servers a repo's .mcp.json names, and JS toolchains)
RUN apt-get update && \
    apt-get install -y git curl bash && \
    curl -fsSL https://deb.nodesource.com/setup_22.x | bash - && \
    apt-get install -y nodejs && \
    rm -rf /var/lib/apt/lists/*

# Install PowerShell 7 — via Microsoft's apt repo. Root scripts run as .ps1
# on both Windows and Linux. Using apt (not the tarball) picks up permissions,
# PATH integration, and transitive deps automatically.
RUN . /etc/os-release && \
    curl -fsSL "https://packages.microsoft.com/config/${ID}/${VERSION_ID}/packages-microsoft-prod.deb" -o /tmp/ms-prod.deb && \
    dpkg -i /tmp/ms-prod.deb && \
    rm /tmp/ms-prod.deb && \
    apt-get update && \
    apt-get install -y powershell && \
    rm -rf /var/lib/apt/lists/*

# Install GitHub CLI — via GitHub's apt repo. Scripts and Claude sessions use
# `gh` for PR/issue/codespace operations (auth comes from env/mounted token).
RUN mkdir -p -m 755 /etc/apt/keyrings && \
    curl -fsSL https://cli.github.com/packages/githubcli-archive-keyring.gpg -o /etc/apt/keyrings/githubcli-archive-keyring.gpg && \
    chmod go+r /etc/apt/keyrings/githubcli-archive-keyring.gpg && \
    echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/githubcli-archive-keyring.gpg] https://cli.github.com/packages stable main" > /etc/apt/sources.list.d/github-cli.list && \
    apt-get update && \
    apt-get install -y gh && \
    rm -rf /var/lib/apt/lists/*

# Install Claude Code system-wide and root-owned, so no session can replace the claude the server
# runs. The native installer's ~/.local/bin/claude is the user's to write, and claude updates itself
# there. npm's global install is root's (/usr/lib/node_modules, linked from /usr/bin/claude), and
# self-update is off: a claude run as godmode could not write it anyway. The managed settings turn it
# off in every claude process too, whose environment the server builds from an allowlist without this ENV.
ENV DISABLE_AUTOUPDATER=1
RUN npm install -g @anthropic-ai/claude-code && \
    npm cache clean --force && \
    mkdir -p /etc/claude-code && \
    echo '{ "env": { "DISABLE_AUTOUPDATER": "1" } }' > /etc/claude-code/managed-settings.json && \
    test "$(command -v claude)" = /usr/bin/claude && \
    test -x /usr/bin/pwsh && \
    claude --version

# The executables the server starts, by full path (see PATH above)
ENV PowerShell__Executable=/usr/bin/pwsh
ENV Claude__Executable=/usr/bin/claude

# The server and its SPA stay root's, read-only to godmode: a session could otherwise rewrite them.
# godmode owns only what the server writes: the roots (ProjectRootsDir, /app/roots by default, where
# a volume goes), the default root's projects when no root is configured, /data, and its home.
WORKDIR /app
COPY --from=dotnet-build /app/publish .

RUN mkdir -p /app/roots /app/projects /data && \
    chown godmode:godmode /app/roots /app/projects /data

EXPOSE 31337

# Unprefixed URLS, not ASPNETCORE_URLS: ASPNETCORE_* is read before appsettings.json, whose
# loopback "Urls" would win. Every request needs the API key. With none configured, the server
# generates one into /home/godmode/.local/share/GodMode.Server/api-key, which is lost when the
# container is replaced unless that directory is a volume: run with -e Authentication__ApiKey=<key>,
# or -v godmode-key:/home/godmode/.local/share/GodMode.Server.
# A browser is let in only from the container's own origin, http://localhost:31337 (published on
# the same port). Opening it by a host name, a LAN address or another published port needs that
# origin in -e Authentication__AllowedOrigins=http://<host>:<port>.
ENV URLS=http://+:31337

USER godmode
# The key file's directory, owner-only and godmode's: a named volume mounted there starts with
# this owner and mode, where one Docker creates itself would be root's and unwritable
RUN mkdir -p -m 700 /home/godmode/.local/share/GodMode.Server
ENTRYPOINT ["dotnet", "GodMode.Server.dll"]

# ── SDK variant ──
# Published as the `:sdk` tag. Layers .NET SDK on top of the runtime image so
# Claude Code (or anyone running inside the container) can `dotnet build` / test.
# Consumers who only need to run the server should use `:latest`; consumers who
# run dev workflows inside the container should use `:sdk`.
FROM runtime AS sdk
USER root
RUN apt-get update && \
    apt-get install -y --no-install-recommends dotnet-sdk-10.0 && \
    rm -rf /var/lib/apt/lists/*
USER godmode
ENTRYPOINT ["dotnet", "GodMode.Server.dll"]
