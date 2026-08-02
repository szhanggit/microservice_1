using System.Text.Json.Serialization;

namespace TransactionGateway.Api.Contracts;

/// <summary>HTTP response shape for GET /api/v1/transactions?from=&to= (TransactionGateway.md §5).</summary>
public sealed record TransactionSearchPageHttpResponse(
    [property: JsonPropertyName("items")] IReadOnlyList<TransactionHttpResponse> Items,
    [property: JsonPropertyName("has_more")] bool HasMore);
