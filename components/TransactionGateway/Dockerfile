# Build context is components/ (one level above this file - see
# docker-compose.yml's `context: ..`), not TransactionGateway/ itself, so the
# shared components/contracts/transaction.proto is reachable at build time.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY contracts/ contracts/
COPY TransactionGateway/ TransactionGateway/

WORKDIR /src/TransactionGateway
RUN dotnet publish src/TransactionGateway.Api/TransactionGateway.Api.csproj -c Release -o /app/publish

# aspnet (not runtime-deps) - this service serves HTTP, unlike TransactionWorker.
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

ENTRYPOINT ["dotnet", "TransactionGateway.Api.dll"]
