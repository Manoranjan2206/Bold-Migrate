# Multi-stage Dockerfile for Bold Migrate (.NET 10 ASP.NET Core single service)

# Stage 1: Build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

# Copy solution and project files first to leverage Docker layer caching
COPY BoldMigrate.slnx ./
COPY src/BoldMigrate.Core/BoldMigrate.Core.csproj src/BoldMigrate.Core/
COPY src/BoldMigrate.Infrastructure/BoldMigrate.Infrastructure.csproj src/BoldMigrate.Infrastructure/
COPY src/BoldMigrate.Engine/BoldMigrate.Engine.csproj src/BoldMigrate.Engine/
COPY src/BoldMigrate.Api/BoldMigrate.Api.csproj src/BoldMigrate.Api/
COPY tests/BoldMigrate.Core.Tests/BoldMigrate.Core.Tests.csproj tests/BoldMigrate.Core.Tests/
COPY tests/BoldMigrate.Engine.Tests/BoldMigrate.Engine.Tests.csproj tests/BoldMigrate.Engine.Tests/

# Restore dependencies
RUN dotnet restore src/BoldMigrate.Api/BoldMigrate.Api.csproj

# Copy all source files and data
COPY src/ src/
COPY data/ data/

# Publish single service release
WORKDIR /source/src/BoldMigrate.Api
RUN dotnet publish -c Release -o /app/publish /p:UseAppHost=false

# Stage 2: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Ensure dependencies are available for Playwright Chromium headless, GDI+ rendering, NTLM auth, and healthcheck
RUN apt-get update && apt-get install -y --no-install-recommends \
    curl \
    gss-ntlmssp \
    libgdiplus \
    fontconfig \
    libnss3 \
    libnspr4 \
    libatk1.0-0 \
    libatk-bridge2.0-0 \
    libcups2 \
    libdrm2 \
    libxkbcommon0 \
    libxcomposite1 \
    libxdamage1 \
    libxfixes3 \
    libxrandr2 \
    libgbm1 \
    libpango-1.0-0 \
    libcairo2 \
    fonts-liberation \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /app/data /app/staging

# Copy published application from build stage
COPY --from=build /app/publish ./
COPY data/compatibility-rules.json ./data/

# Pre-install Playwright Chromium for headless Step 4 validation & run as non-root user
ENV PLAYWRIGHT_BROWSERS_PATH=/ms-playwright
RUN if [ -x /app/.playwright/node/linux-x64/node ]; then \
        /app/.playwright/node/linux-x64/node /app/.playwright/package/cli.js install chromium; \
    elif [ -x /app/.playwright/node/linux-arm64/node ]; then \
        /app/.playwright/node/linux-arm64/node /app/.playwright/package/cli.js install chromium; \
    fi \
    && groupadd -r boldmigrate \
    && useradd -r -g boldmigrate -m -d /home/boldmigrate boldmigrate \
    && mkdir -p /app/data /app/staging /app/logs /ms-playwright \
    && chown -R boldmigrate:boldmigrate /app /ms-playwright

USER boldmigrate

# Environment settings
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production
ENV BoldMigrate__StagingRootPath=/app/staging
ENV BoldMigrate__DatabasePath=/app/data/boldmigrate.db
ENV BoldMigrate__LogsPath=/app/logs

EXPOSE 8080

ENTRYPOINT ["dotnet", "BoldMigrate.dll"]
