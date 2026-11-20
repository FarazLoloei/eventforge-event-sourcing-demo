# Optional full-stack image for the EventForge API. Native `dotnet run` remains the default workflow.
# Build context: repository root. No credentials are baked in; supply ConnectionStrings__EventForge at run time.

ARG DOTNET_SDK_VERSION=10.0.404
ARG DOTNET_RUNTIME_TAG=10.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_SDK_VERSION} AS build
WORKDIR /src

# Restore first so the package layer is cached until a project file changes.
COPY global.json ./
COPY src/EventForge.Api/EventForge.Api.csproj src/EventForge.Api/
COPY src/EventForge.Workflow/EventForge.Workflow.csproj src/EventForge.Workflow/
COPY src/EventForge.Infrastructure/EventForge.Infrastructure.csproj src/EventForge.Infrastructure/
COPY src/EventForge.Simulation/EventForge.Simulation.csproj src/EventForge.Simulation/
RUN dotnet restore src/EventForge.Api/EventForge.Api.csproj

COPY src/ src/
RUN dotnet publish src/EventForge.Api/EventForge.Api.csproj -c Release --no-restore -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_RUNTIME_TAG} AS final
WORKDIR /app
COPY --from=build /app/publish ./

# Run as the non-root "app" user shipped in the .NET 10 images; the image listens on port 8080.
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "EventForge.Api.dll"]
