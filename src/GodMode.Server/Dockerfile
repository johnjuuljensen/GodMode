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
COPY src/GodMode.AI/GodMode.AI.csproj src/GodMode.AI/
RUN dotnet restore src/GodMode.Server/GodMode.Server.csproj

# Copy source and publish
COPY src/GodMode.Server/ src/GodMode.Server/
COPY src/GodMode.Shared/ src/GodMode.Shared/
COPY src/GodMode.ProjectFiles/ src/GodMode.ProjectFiles/
COPY src/GodMode.AI/ src/GodMode.AI/
RUN dotnet publish src/GodMode.Server/GodMode.Server.csproj -c Release -o /app/publish -p:PublishReactClient=false

# Copy React build output into wwwroot
COPY --from=react-build /app/dist /app/publish/wwwroot/

# ── Runtime ──
FROM mcr.microsoft.com/dotnet/aspnet:10.0

ENV HOME=/root
ENV PATH="/root/.local/bin:/root/.claude/local/bin:${PATH}"

# Install system deps + Node.js (needed for npx MCP servers)
RUN apt-get update && \
    apt-get install -y git curl && \
    curl -fsSL https://deb.nodesource.com/setup_22.x | bash - && \
    apt-get install -y nodejs && \
    rm -rf /var/lib/apt/lists/*

# Install Claude Code CLI
RUN curl -fsSL https://claude.ai/install.sh | sh && \
    echo "Claude installed at: $(which claude)" && \
    claude --version

WORKDIR /app
COPY --from=dotnet-build /app/publish .

RUN mkdir -p /app/projects /data

EXPOSE 31337

ENV ASPNETCORE_URLS=http://+:31337

ENTRYPOINT ["dotnet", "GodMode.Server.dll"]
