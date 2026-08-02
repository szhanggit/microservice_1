# KEDA.md §9: multi-stage - SDK to build/publish, runtime (not aspnet) to
# run, since this is a worker, not a web app.
#
# Build context is components/ (one level above this file - see
# docker-compose.yml's `context: ..`), so the shared components/ShardRouting
# library (database.md §10) is reachable at build time.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ShardRouting/ ShardRouting/
COPY TransactionWorker/ TransactionWorker/

WORKDIR /src/TransactionWorker
RUN dotnet publish src/TransactionWorker/TransactionWorker.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/runtime:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "TransactionWorker.dll"]
