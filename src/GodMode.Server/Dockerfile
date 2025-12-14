# Build stage
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy project files
COPY ["GodMode.Server/GodMode.Server.csproj", "GodMode.Server/"]
COPY ["GodMode.Shared/GodMode.Shared.csproj", "GodMode.Shared/"]

# Restore dependencies
RUN dotnet restore "GodMode.Server/GodMode.Server.csproj"

# Copy source code
COPY . .

# Build and publish
WORKDIR "/src/GodMode.Server"
RUN dotnet publish "GodMode.Server.csproj" -c Release -o /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final

# Install git and claude CLI (if available via package manager)
RUN apt-get update && apt-get install -y \
    git \
    curl \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/publish .

# Create projects directory
RUN mkdir -p /app/projects

# Expose port
EXPOSE 31337

# Set environment variables
ENV ASPNETCORE_URLS=http://+:31337
ENV ProjectsPath=/app/projects

ENTRYPOINT ["dotnet", "GodMode.Server.dll"]
