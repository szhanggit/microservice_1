namespace ClientTester;

/// <summary>
/// Builds POST bodies for /api/v1/transactions. Each call gets a unique
/// transaction_no - TransactionGateway's Redis-backed idempotency cache
/// (TransactionGateway.md) would otherwise turn a repeated transaction_no
/// into a cheap cache-hit short-circuit instead of a real submission that
/// flows through Service -> SQS -> Worker -> Postgres, defeating the point
/// of a throughput test.
///
/// Builds the JSON directly via string interpolation rather than
/// System.Text.Json serialization - the payload shape never varies, so
/// there's no reason to pay object-model/serializer overhead per request
/// when the goal is maximum request rate.
/// </summary>
internal sealed class PayloadFactory
{
    private const string Amount = "100.00";

    private readonly string _runId = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
    private long _sequence;

    public string NextPayload()
    {
        var sequence = Interlocked.Increment(ref _sequence);
        var transactionNo = $"LOADTEST-{_runId}-{sequence}";
        var transactionDatetime = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

        return $$"""
            {"transaction_no":"{{transactionNo}}","transaction_datetime":"{{transactionDatetime}}","amount":"{{Amount}}","type":"PAYMENT","status":"COMPLETED","currency":"USD"}
            """;
    }
}
