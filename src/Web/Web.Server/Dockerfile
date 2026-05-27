# syntax=docker/dockerfile:1
# Build context is SPO/ColdStorage/src/ (set via <DockerfileContext>..\..</DockerfileContext>).

# ---------- Stage 1: build the React SPA ----------
FROM node:20-bookworm-slim AS spa
WORKDIR /spa
COPY Web/web.client/package.json Web/web.client/package-lock.json ./
RUN npm ci
COPY Web/web.client/ ./
RUN npm run build

# ---------- Stage 2: build & publish the .NET app ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# Copy props first for better layer caching
COPY Directory.Build.props Directory.Packages.props ./

# Copy project files for restore
COPY Web/Web.Server/Web.Server.csproj         Web/Web.Server/
COPY Migration.Engine/Migration.Engine.csproj Migration.Engine/
COPY Entities/Entities.csproj                 Entities/
COPY Models/Models.csproj                     Models/

# Skip the web.client esproj reference - the SPA is built in stage 1
RUN dotnet restore "Web/Web.Server/Web.Server.csproj" \
    -p:BuildProjectReferences=false \
    /p:SkipNpmInstall=true

# Copy sources
COPY Web/Web.Server/   Web/Web.Server/
COPY Migration.Engine/ Migration.Engine/
COPY Entities/         Entities/
COPY Models/           Models/

# Bring in the prebuilt SPA assets
COPY --from=spa /spa/dist/ Web/Web.Server/wwwroot/

WORKDIR /src/Web/Web.Server
RUN dotnet publish "Web.Server.csproj" \
    -c $BUILD_CONFIGURATION \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false \
    /p:BuildProjectReferences=true

# ---------- Stage 3: runtime ----------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
USER $APP_UID
EXPOSE 8080

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Web.Server.dll"]
