using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using TransactionWorker.Application;
using TransactionWorker.Infrastructure;
using TransactionWorker.Infrastructure.Options;

namespace TransactionWorker.UnitTests.Infrastructure;

public class SqsListenerServiceTests
{
    private const string QueueUrl = "https://sqs.example/TransactionQueue-develop";

    private const string PayloadJson =
        """{"transaction_no":"TXN-1","transaction_datetime":"2026-07-31T12:00:00Z","amount":"100.00","type":"PAYMENT","status":"PENDING","currency":"USD"}""";

    private readonly Mock<IAmazonSQS> _sqs = new();
    private readonly Mock<IMessageProcessor> _processor = new();
    private readonly IServiceScopeFactory _scopeFactory;

    public SqsListenerServiceTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_processor.Object);
        _scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private SqsListenerService CreateService() => new(
        _sqs.Object,
        _scopeFactory,
        Options.Create(new SqsOptions { QueueUrl = QueueUrl }),
        NullLogger<SqsListenerService>.Instance);

    [Fact]
    public async Task ProcessMessageAsync_deletes_and_completes_a_freshly_claimed_message()
    {
        _processor.Setup(p => p.ClaimNewMessageAsync("TXN-1", PayloadJson, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ClaimOutcome.Claimed);

        var message = new Message { MessageId = "m1", Body = PayloadJson, ReceiptHandle = "rh1" };
        await CreateService().ProcessMessageAsync(message, CancellationToken.None);

        _sqs.Verify(s => s.DeleteMessageAsync(QueueUrl, "rh1", It.IsAny<CancellationToken>()), Times.Once);
        _processor.Verify(p => p.CompleteProcessingAsync("TXN-1", PayloadJson, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessMessageAsync_deletes_but_does_not_complete_a_duplicate_delivery()
    {
        // KEDA.md §5 step 3: safe to delete regardless of claim outcome -
        // AlreadyClaimed just means another instance already owns the work.
        _processor.Setup(p => p.ClaimNewMessageAsync("TXN-1", PayloadJson, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ClaimOutcome.AlreadyClaimed);

        var message = new Message { MessageId = "m1", Body = PayloadJson, ReceiptHandle = "rh1" };
        await CreateService().ProcessMessageAsync(message, CancellationToken.None);

        _sqs.Verify(s => s.DeleteMessageAsync(QueueUrl, "rh1", It.IsAny<CancellationToken>()), Times.Once);
        _processor.Verify(p => p.CompleteProcessingAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessMessageAsync_leaves_a_malformed_message_undeleted_for_SQS_redelivery()
    {
        var message = new Message { MessageId = "m1", Body = "not json", ReceiptHandle = "rh1" };

        await CreateService().ProcessMessageAsync(message, CancellationToken.None);

        _sqs.Verify(s => s.DeleteMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _processor.Verify(p => p.ClaimNewMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessMessageAsync_leaves_an_empty_transaction_no_undeleted_for_SQS_redelivery()
    {
        const string emptyTransactionNoJson =
            """{"transaction_no":"","transaction_datetime":"2026-07-31T12:00:00Z","amount":"100.00","type":"PAYMENT","status":"PENDING","currency":"USD"}""";
        var message = new Message { MessageId = "m1", Body = emptyTransactionNoJson, ReceiptHandle = "rh1" };

        await CreateService().ProcessMessageAsync(message, CancellationToken.None);

        _sqs.Verify(s => s.DeleteMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
