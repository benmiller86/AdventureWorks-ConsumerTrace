# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY nuget.config ./
COPY src/AdventureWorks.Web/AdventureWorks.Web.csproj src/AdventureWorks.Web/
RUN dotnet restore src/AdventureWorks.Web/AdventureWorks.Web.csproj

COPY src/ src/
RUN dotnet publish src/AdventureWorks.Web/AdventureWorks.Web.csproj -c Release -o /app/publish --no-restore

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
COPY --from=build /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "AdventureWorks.Web.dll"]
