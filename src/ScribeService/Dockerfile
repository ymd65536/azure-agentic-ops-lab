# Build from the repository root so the shared BuildingBlocks projects are
# available:
#   docker build -f src/ScribeService/Dockerfile -t scribe-service:local .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/ src/

RUN dotnet publish src/ScribeService/ScribeService.csproj \
    --configuration Release \
    --output /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .

# Run as the non-root user provided by the base image and expose a
# non-privileged port so the container works with a restrictive
# securityContext (runAsNonRoot, readOnlyRootFilesystem).
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER $APP_UID

ENTRYPOINT ["dotnet", "AzureAgenticOps.ScribeService.dll"]
