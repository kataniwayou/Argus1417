# Build stage
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy local NuGet packages (relative to build context: argus/)
COPY ["src/nuget-packages/", "/nuget-packages/"]

# Copy csproj and restore dependencies
COPY ["src/Argus/Argus.csproj", "./"]
RUN dotnet restore --source /nuget-packages --source https://api.nuget.org/v3/index.json

# Copy source and build
COPY ["src/Argus/", "./"]
RUN dotnet publish -c Release -o /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

# Create non-root user for security
RUN adduser --disabled-password --gecos "" --uid 1000 argus
USER argus

COPY --from=build /app/publish .

# Expose port 8080 for all endpoints (health + alert receiver)
EXPOSE 8080

# Health check
HEALTHCHECK --interval=30s --timeout=10s --start-period=5s --retries=3 \
    CMD curl -f http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "Argus.dll"]

