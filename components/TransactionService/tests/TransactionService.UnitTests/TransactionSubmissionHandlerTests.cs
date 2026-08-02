using Moq;
using TransactionService.Application;

namespace TransactionService.UnitTests;

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
    public async Task Valid_fresh_submission_is_sent_and_marked_processed()
    {
        var validator = new Mock<ITransactionValidator>();
        validator.Setup(v => v.Validate(Sample)).Returns((string?)null);

        var guard = new Mock<IIdempotencyGuard>();
        guard.Setup(g => g.HasBeenProcessedAsync(Sample.TransactionNo, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var forwarder = new Mock<ISqsMessageForwarder>();

        var handler = CreateHandler(validator.Object, guard.Object, forwarder.Object);

        var result = await handler.HandleAsync(Sample, CancellationToken.None);

        Assert.Equal(SubmissionOutcome.Accepted, result.Outcome);
        forwarder.Verify(f => f.SendAsync(Sample, It.IsAny<CancellationToken>()), Times.Once);
        guard.Verify(g => g.MarkProcessedAsync(Sample.TransactionNo, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Dedup_hit_short_circuits_without_sending()
    {
        var validator = new Mock<ITransactionValidator>();
        validator.Setup(v => v.Validate(Sample)).Returns((string?)null);

        var guard = new Mock<IIdempotencyGuard>();
        guard.Setup(g => g.HasBeenProcessedAsync(Sample.TransactionNo, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var forwarder = new Mock<ISqsMessageForwarder>();

        var handler = CreateHandler(validator.Object, guard.Object, forwarder.Object);

        var result = await handler.HandleAsync(Sample, CancellationToken.None);

        Assert.Equal(SubmissionOutcome.Accepted, result.Outcome);
        forwarder.Verify(f => f.SendAsync(It.IsAny<TransactionSubmission>(), It.IsAny<CancellationToken>()), Times.Never);
        guard.Verify(g => g.MarkProcessedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Invalid_field_short_circuits_before_cache_or_sqs()
    {
        var validator = new Mock<ITransactionValidator>();
        validator.Setup(v => v.Validate(Sample)).Returns("currency 'XYZ' is not a recognized ISO 4217 code");

        var guard = new Mock<IIdempotencyGuard>();
        var forwarder = new Mock<ISqsMessageForwarder>();

        var handler = CreateHandler(validator.Object, guard.Object, forwarder.Object);

        var result = await handler.HandleAsync(Sample, CancellationToken.None);

        Assert.Equal(SubmissionOutcome.ValidationFailed, result.Outcome);
        Assert.Equal("currency 'XYZ' is not a recognized ISO 4217 code", result.ErrorMessage);
        guard.Verify(g => g.HasBeenProcessedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        forwarder.Verify(f => f.SendAsync(It.IsAny<TransactionSubmission>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Cache_unavailable_fails_open_and_still_sends()
    {
        var validator = new Mock<ITransactionValidator>();
        validator.Setup(v => v.Validate(Sample)).Returns((string?)null);

        var guard = new Mock<IIdempotencyGuard>();
        guard.Setup(g => g.HasBeenProcessedAsync(Sample.TransactionNo, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("redis unavailable"));

        var forwarder = new Mock<ISqsMessageForwarder>();

        var handler = CreateHandler(validator.Object, guard.Object, forwarder.Object);

        var result = await handler.HandleAsync(Sample, CancellationToken.None);

        Assert.Equal(SubmissionOutcome.Accepted, result.Outcome);
        forwarder.Verify(f => f.SendAsync(Sample, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SendMessage_failure_returns_SendFailed_and_does_not_mark_processed()
    {
        var validator = new Mock<ITransactionValidator>();
        validator.Setup(v => v.Validate(Sample)).Returns((string?)null);

        var guard = new Mock<IIdempotencyGuard>();
        guard.Setup(g => g.HasBeenProcessedAsync(Sample.TransactionNo, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var forwarder = new Mock<ISqsMessageForwarder>();
        forwarder.Setup(f => f.SendAsync(Sample, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("SQS unreachable"));

        var handler = CreateHandler(validator.Object, guard.Object, forwarder.Object);

        var result = await handler.HandleAsync(Sample, CancellationToken.None);

        Assert.Equal(SubmissionOutcome.SendFailed, result.Outcome);
        guard.Verify(g => g.MarkProcessedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Failed_marker_write_after_successful_send_is_swallowed()
    {
        var validator = new Mock<ITransactionValidator>();
        validator.Setup(v => v.Validate(Sample)).Returns((string?)null);

        var guard = new Mock<IIdempotencyGuard>();
        guard.Setup(g => g.HasBeenProcessedAsync(Sample.TransactionNo, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        guard.Setup(g => g.MarkProcessedAsync(Sample.TransactionNo, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("redis unavailable"));

        var forwarder = new Mock<ISqsMessageForwarder>();

        var handler = CreateHandler(validator.Object, guard.Object, forwarder.Object);

        var result = await handler.HandleAsync(Sample, CancellationToken.None);

        Assert.Equal(SubmissionOutcome.Accepted, result.Outcome);
    }

    private static TransactionSubmissionHandler CreateHandler(
        ITransactionValidator validator, IIdempotencyGuard guard, ISqsMessageForwarder forwarder) =>
        new(validator, guard, forwarder, Microsoft.Extensions.Logging.Abstractions.NullLogger<TransactionSubmissionHandler>.Instance);
}
