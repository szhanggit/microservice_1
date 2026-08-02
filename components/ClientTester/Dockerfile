# No shared components/ (ShardRouting, contracts) dependency - unlike the
# other 3 services, ClientTester only speaks plain HTTP/JSON to the public
# ALB URL, so the build context is this directory alone (see
# docker-compose.yml's `context: .`).
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish ClientTester.csproj -c Release -o /app/publish

# runtime (not aspnet) - this is an outbound HTTP client, not a web app.
FROM mcr.microsoft.com/dotnet/runtime:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "ClientTester.dll"]
