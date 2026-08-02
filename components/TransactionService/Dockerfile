# Build context is components/ (one level above this file - see
# docker-compose.yml's `context: ..`), not TransactionService/ itself, so the
# shared components/contracts/transaction.proto and components/ShardRouting
# (TransactionService.Application's search path, database.md §10) are
# reachable at build time.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY contracts/ contracts/
COPY ShardRouting/ ShardRouting/
COPY TransactionService/ TransactionService/

WORKDIR /src/TransactionService
RUN dotnet publish src/TransactionService.Grpc/TransactionService.Grpc.csproj -c Release -o /app/publish

# aspnet (not runtime-deps) - Kestrel/HTTP2 is needed to host gRPC
# (TransactionService.md §7).
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

# Two ports, not ASPNETCORE_URLS - the gRPC port (8080, HTTP/2-only) and the
# Kubernetes-probe port (8081, HTTP/1.1-only) need different Kestrel
# Protocols settings, which only appsettings.json's named Kestrel:Endpoints
# section can express (a single ASPNETCORE_URLS value can't). See that
# file's comment for why - HTTP/2 without TLS needs to be explicit per
# endpoint; Kestrel doesn't auto-negotiate cleartext HTTP/1.1 vs HTTP/2 per
# connection here.
EXPOSE 8080
EXPOSE 8081

ENTRYPOINT ["dotnet", "TransactionService.Grpc.dll"]
