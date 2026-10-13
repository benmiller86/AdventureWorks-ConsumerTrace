# Restore stage: shared by dev and build so packages are cached in a layer
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS restore
WORKDIR /src

COPY nuget.config ./
COPY src/AdventureWorks.Web/AdventureWorks.Web.csproj src/AdventureWorks.Web/
COPY src/EfCore.ConsumerTrace/EfCore.ConsumerTrace.csproj src/EfCore.ConsumerTrace/
RUN dotnet restore src/AdventureWorks.Web/AdventureWorks.Web.csproj

# Dev stage: source is bind-mounted by docker-compose and run under dotnet watch for hot reload
FROM restore AS dev
WORKDIR /src/src/AdventureWorks.Web
ENV ASPNETCORE_ENVIRONMENT=Development \
    ASPNETCORE_HTTP_PORTS=8080 \
    # Windows bind mounts don't raise inotify events inside Linux containers, so poll for changes
    DOTNET_USE_POLLING_FILE_WATCHER=true \
    DOTNET_WATCH_SUPPRESS_EMOJIS=1
EXPOSE 8080
ENTRYPOINT ["dotnet", "watch", "run", "--non-interactive", "--no-launch-profile"]

# Build stage
FROM restore AS build
COPY src/ src/
RUN dotnet publish src/AdventureWorks.Web/AdventureWorks.Web.csproj -c Release -o /app/publish --no-restore

# Runtime stage (production image: docker build --target final .)
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
COPY --from=build /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "AdventureWorks.Web.dll"]
