FROM mcr.microsoft.com/dotnet/sdk:10.0-preview AS build
WORKDIR /src

# Copy central package management
COPY Directory.Packages.props ./
COPY src/Directory.Build.props src/

# Copy project file and restore
COPY src/GodMode.Vault/GodMode.Vault.csproj src/GodMode.Vault/
RUN dotnet restore src/GodMode.Vault/GodMode.Vault.csproj

# Copy source and publish
COPY src/GodMode.Vault/ src/GodMode.Vault/
RUN dotnet publish src/GodMode.Vault/GodMode.Vault.csproj -c Release -o /app --no-restore

# --- Runtime ---
FROM mcr.microsoft.com/dotnet/aspnet:10.0-preview
WORKDIR /app

# Non-root user
RUN groupadd -r vault && useradd -r -g vault vault

# Persistent storage mount point
RUN mkdir -p /data/secrets && chown vault:vault /data/secrets
VOLUME /data/secrets

COPY --from=build /app .

USER vault

ENV ASPNETCORE_URLS=http://+:8080
ENV VAULT_STORAGE_PATH=/data/secrets

EXPOSE 8080

ENTRYPOINT ["dotnet", "GodMode.Vault.dll"]
