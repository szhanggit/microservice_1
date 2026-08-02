using System.Text.Json.Serialization;

namespace TransactionGateway.Api.Contracts;

/// <summary>HTTP response shape for both search endpoints (TransactionGateway.md §5).</summary>
public sealed record TransactionHttpResponse(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("transaction_no")] string TransactionNo,
    [property: JsonPropertyName("transaction_datetime")] string TransactionDatetime,
    [property: JsonPropertyName("amount")] string Amount,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("system_datetime")] string SystemDatetime);
