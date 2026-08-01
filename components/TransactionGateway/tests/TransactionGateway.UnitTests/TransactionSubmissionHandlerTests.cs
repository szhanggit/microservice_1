using Moq;
using TransactionGateway.Application;

namespace TransactionGateway.UnitTests;

public class TransactionSubmissionHandlerTests
{
    private static readonly TransactionSubmission Sample = new(
        TransactionNo: "TXN-1",
        TransactionDatetime: "2026-07-31T12:00:00Z",
        Amount: "100.00",
        Type: "PAYMENT",
        Status: "PENDING",
        Currency: "USD");

    [Fact]
    public async Task Fresh_submission_is_forwarded_and_marked_processed()
    {
        var guard = new Mock<IIdempotencyGuard>();
        guard.Setup(g => g.HasBeenProcessedAsync(Sample.TransactionNo, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var forwarder = new Mock<ITransactionForwarder>();
        forwarder.Setup(f => f.ForwardAsync(Sample, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var handler = CreateHandler(guard.Object, forwarder.Object);

        var result = await handler.HandleAsync(Sample, CancellationToken.None);

        Assert.True(result);
        forwarder.Verify(f => f.ForwardAsync(Sample, It.IsAny<CancellationToken>()), Times.Once);
        guard.Verify(g => g.MarkProcessedAsync(Sample.TransactionNo, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Dedup_hit_short_circuits_without_forwarding()
    {
        var guard = new Mock<IIdempotencyGuard>();
        guard.Setup(g => g.HasBeenProcessedAsync(Sample.TransactionNo, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var forwarder = new Mock<ITransactionForwarder>();

        var handler = CreateHandler(guard.Object, forwarder.Object);

        var result = await handler.HandleAsync(Sample, CancellationToken.None);

        Assert.True(result);
        forwarder.Verify(f => f.ForwardAsync(It.IsAny<TransactionSubmission>(), It.IsAny<CancellationToken>()), Times.Never);
        guard.Verify(g => g.MarkProcessedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Cache_unavailable_fails_open_and_still_forwards()
    {
        var guard = new Mock<IIdempotencyGuard>();
        guard.Setup(g => g.HasBeenProcessedAsync(Sample.TransactionNo, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("redis unavailable"));

        var forwarder = new Mock<ITransactionForwarder>();
        forwarder.Setup(f => f.ForwardAsync(Sample, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var handler = CreateHandler(guard.Object, forwarder.Object);

        var result = await handler.HandleAsync(Sample, CancellationToken.None);

        Assert.True(result);
        forwarder.Verify(f => f.ForwardAsync(Sample, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Grpc_failure_returns_false_and_does_not_mark_processed()
    {
        var guard = new Mock<IIdempotencyGuard>();
        guard.Setup(g => g.HasBeenProcessedAsync(Sample.TransactionNo, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var forwarder = new Mock<ITransactionForwarder>();
        forwarder.Setup(f => f.ForwardAsync(Sample, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("transaction service unreachable"));

        var handler = CreateHandler(guard.Object, forwarder.Object);

        var result = await handler.HandleAsync(Sample, CancellationToken.None);

        Assert.False(result);
        guard.Verify(g => g.MarkProcessedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Rejected_forward_returns_false_and_does_not_mark_processed()
    {
        var guard = new Mock<IIdempotencyGuard>();
        guard.Setup(g => g.HasBeenProcessedAsync(Sample.TransactionNo, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var forwarder = new Mock<ITransactionForwarder>();
        forwarder.Setup(f => f.ForwardAsync(Sample, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var handler = CreateHandler(guard.Object, forwarder.Object);

        var result = await handler.HandleAsync(Sample, CancellationToken.None);

        Assert.False(result);
        guard.Verify(g => g.MarkProcessedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Failed_marker_write_after_successful_forward_is_swallowed()
    {
        var guard = new Mock<IIdempotencyGuard>();
        guard.Setup(g => g.HasBeenProcessedAsync(Sample.TransactionNo, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        guard.Setup(g => g.MarkProcessedAsync(Sample.TransactionNo, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("redis unavailable"));

        var forwarder = new Mock<ITransactionForwarder>();
        forwarder.Setup(f => f.ForwardAsync(Sample, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var handler = CreateHandler(guard.Object, forwarder.Object);

        var result = await handler.HandleAsync(Sample, CancellationToken.None);

        Assert.True(result);
    }

    private static TransactionSubmissionHandler CreateHandler(IIdempotencyGuard guard, ITransactionForwarder forwarder) =>
        new(guard, forwarder, Microsoft.Extensions.Logging.Abstractions.NullLogger<TransactionSubmissionHandler>.Instance);
}
