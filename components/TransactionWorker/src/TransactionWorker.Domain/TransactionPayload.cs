using System.Text.Json.Serialization;

namespace TransactionWorker.Domain;

/// <summary>
/// Shape of the JSON body TransactionService puts on SQS (TransactionService.md
/// §5) - no id/shard_id, those are computed here at insert time (database.md §5).
/// </summary>
public sealed record TransactionPayload(
    [property: JsonPropertyName("transaction_no")] string TransactionNo,
    [property: JsonPropertyName("transaction_datetime")] string TransactionDatetime,
    [property: JsonPropertyName("amount")] string Amount,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("currency")] string Currency);
