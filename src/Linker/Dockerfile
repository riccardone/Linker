# Base runtime
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS base
WORKDIR /app

# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# Restore step
COPY src/Linker/Linker.csproj Linker/
COPY src/Linker.Core/Linker.Core.csproj Linker.Core/
RUN dotnet restore Linker/Linker.csproj

# Copy source
COPY src/ .

# Build app
WORKDIR /src/Linker
RUN dotnet build Linker.csproj -c $BUILD_CONFIGURATION -o /app/build

# Publish step
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish Linker.csproj -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# Final runtime image
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app

# Optional: create folders for mount compatibility
RUN mkdir -p /config /certs /data

COPY --from=publish /app/publish .

# Optional default config (can be overridden with mount or env var)
COPY src/Linker/appsettings.json /config/appsettings.json

# Recommended: default env for config file (override at runtime)
ENV LINKER_CONFIG_PATH=/config/appsettings.json

# Runtime command
ENTRYPOINT ["dotnet", "Linker.dll"]