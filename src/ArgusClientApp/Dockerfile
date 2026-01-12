# Build stage
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy local NuGet packages and project files
COPY ["nuget-packages/", "/nuget-packages/"]
COPY ["ArgusApi/ArgusApi.csproj", "ArgusApi/"]
COPY ["ArgusClientApp/ArgusClientApp.csproj", "ArgusClientApp/"]

# Restore dependencies using local packages and nuget.org
WORKDIR /src/ArgusClientApp
RUN dotnet restore "ArgusClientApp.csproj" --source /nuget-packages --source https://api.nuget.org/v3/index.json

# Copy source and build
COPY ["ArgusApi/", "/src/ArgusApi/"]
COPY ["ArgusClientApp/", "/src/ArgusClientApp/"]
RUN dotnet publish -c Release -o /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

# Create non-root user for security
RUN adduser --disabled-password --gecos "" --uid 1000 argusclient
USER argusclient

COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "ArgusClientApp.dll"]

