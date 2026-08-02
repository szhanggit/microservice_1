using System.Text.Json.Serialization;

namespace TransactionGateway.Api.Contracts;

/// <summary>HTTP contract for POST /api/v1/transactions (TransactionGateway.md §5).</summary>
public sealed record SubmitTransactionHttpRequest(
    [property: JsonPropertyName("transaction_no")] string TransactionNo,
    [property: JsonPropertyName("transaction_datetime")] string TransactionDatetime,
    [property: JsonPropertyName("amount")] string Amount,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("currency")] string Currency);
