using Contracts.Transactions;
using Grpc.Core;
using Polly;
using Polly.Retry;
using TransactionGateway.Application;
using TransactionGateway.Infrastructure;
using TransactionGateway.UnitTests.TestDoubles;

namespace TransactionGateway.UnitTests;

public class GrpcTransactionForwarderTests
{
    private static readonly TransactionSubmission Sample = new(
        TransactionNo: "TXN-1",
        TransactionDatetime: "2026-07-31T12:00:00Z",
        Amount: "100.00",
        Type: "PAYMENT",
        Status: "PENDING",
        Currency: "USD");

    // Same retry policy as GrpcTransactionForwarder.BuildDefaultRetryPipeline,
    // but with zero delay so the test doesn't wait through real backoff.
    private static ResiliencePipeline FastRetryPipeline() =>
        new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<RpcException>(
                    ex => ex.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded),
                MaxRetryAttempts = 3,
                Delay = TimeSpan.Zero,
            })
            .Build();

    [Fact]
    public async Task Succeeds_after_transient_failures_within_retry_budget()
    {
        var invoker = new SequencedCallInvoker(
            () => throw new RpcException(new Status(StatusCode.Unavailable, "down")),
            () => throw new RpcException(new Status(StatusCode.Unavailable, "down")),
            () => new SubmitTransactionResponse { Accepted = true });

        var client = new TransactionService.TransactionServiceClient(invoker);
        var forwarder = new GrpcTransactionForwarder(client, FastRetryPipeline());

        var result = await forwarder.ForwardAsync(Sample, CancellationToken.None);

        Assert.True(result);
        Assert.Equal(3, invoker.CallCount);
    }

    [Fact]
    public async Task Throws_once_the_retry_budget_is_exhausted()
    {
        var invoker = new SequencedCallInvoker(
            () => throw new RpcException(new Status(StatusCode.Unavailable, "down")));

        var client = new TransactionService.TransactionServiceClient(invoker);
        var forwarder = new GrpcTransactionForwarder(client, FastRetryPipeline());

        await Assert.ThrowsAsync<RpcException>(() => forwarder.ForwardAsync(Sample, CancellationToken.None));
        Assert.Equal(4, invoker.CallCount); // 1 initial attempt + 3 retries
    }

    [Fact]
    public async Task Does_not_retry_non_transient_statuses()
    {
        var invoker = new SequencedCallInvoker(
            () => throw new RpcException(new Status(StatusCode.InvalidArgument, "bad request")));

        var client = new TransactionService.TransactionServiceClient(invoker);
        var forwarder = new GrpcTransactionForwarder(client, FastRetryPipeline());

        await Assert.ThrowsAsync<RpcException>(() => forwarder.ForwardAsync(Sample, CancellationToken.None));
        Assert.Equal(1, invoker.CallCount);
    }
}
