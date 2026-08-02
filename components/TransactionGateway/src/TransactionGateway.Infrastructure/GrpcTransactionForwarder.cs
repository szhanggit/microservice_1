using Contracts.Transactions;
using Grpc.Core;
using Polly;
using Polly.Retry;
using TransactionGateway.Application;

namespace TransactionGateway.Infrastructure;

/// <summary>
/// Forwards a submission to TransactionService.SubmitTransaction over gRPC
/// (TransactionGateway.md §5), retrying only on transport-level transience -
/// Unavailable (connection issue) and DeadlineExceeded (slow call). Any other
/// RpcException (e.g. InvalidArgument from a validation failure) is not
/// retried and propagates immediately.
/// </summary>
public sealed class GrpcTransactionForwarder : ITransactionForwarder
{
    private readonly TransactionService.TransactionServiceClient _client;
    private readonly ResiliencePipeline _retryPipeline;

    public GrpcTransactionForwarder(TransactionService.TransactionServiceClient client)
        : this(client, BuildDefaultRetryPipeline())
    {
    }

    // Internal constructor so tests can inject a fast/no-op pipeline instead
    // of waiting through real retry backoff.
    internal GrpcTransactionForwarder(TransactionService.TransactionServiceClient client, ResiliencePipeline retryPipeline)
    {
        _client = client;
        _retryPipeline = retryPipeline;
    }

    public async Task<bool> ForwardAsync(TransactionSubmission submission, CancellationToken cancellationToken)
    {
        var request = new SubmitTransactionRequest
        {
            TransactionNo = submission.TransactionNo,
            TransactionDatetime = submission.TransactionDatetime,
            Amount = submission.Amount,
            Type = submission.Type,
            Status = submission.Status,
            Currency = submission.Currency,
        };

        var response = await _retryPipeline.ExecuteAsync(
            async ct => await _client.SubmitTransactionAsync(request, cancellationToken: ct).ResponseAsync,
            cancellationToken);

        return response.Accepted;
    }

    internal static ResiliencePipeline BuildDefaultRetryPipeline() =>
        new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<RpcException>(
                    ex => ex.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded),
                MaxRetryAttempts = 3,
                BackoffType = DelayBackoffType.Exponential,
                Delay = TimeSpan.FromMilliseconds(200),
            })
            .Build();
}
