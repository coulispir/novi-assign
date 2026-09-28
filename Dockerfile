# Production image for the API host. Used by docker-compose.lb.yml; the default docker-compose.yml runs from source.

# Build stage: restore first so the package layer stays cached until a project file changes
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/App.Host/App.Host.csproj src/App.Host/
COPY src/Apis/Wallet.Api/Wallet.Api.csproj src/Apis/Wallet.Api/
COPY src/Core.Service/Core.Service.csproj src/Core.Service/
COPY src/Ecb.Gateway/Ecb.Gateway.csproj src/Ecb.Gateway/
RUN dotnet restore src/App.Host/App.Host.csproj

COPY src/ src/
RUN dotnet publish src/App.Host/App.Host.csproj --configuration Release --no-restore --output /app/publish

# Runtime stage: ASP.NET runtime only, no SDK, running as the image's built-in non-root user
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish .

# Serilog's rolling file sink writes to /app/logs, which the non-root user must own
RUN mkdir logs && chown "$APP_UID" logs
USER $APP_UID

EXPOSE 8080
ENTRYPOINT ["dotnet", "App.Host.dll"]
