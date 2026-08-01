using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Options;
using Moq;
using TransactionService.Application;
using TransactionService.Infrastructure;
using TransactionService.Infrastructure.Options;

namespace TransactionService.UnitTests;

public class SqsMessageForwarderTests
{
    private static readonly TransactionSubmission Sample = new(
        TransactionNo: "TXN-1",
        TransactionDatetime: "2026-07-31T12:00:00Z",
        Amount: "100.00",
        Type: "PAYMENT",
        Status: "PENDING",
        Currency: "USD");

    [Fact]
    public async Task SendAsync_sends_the_raw_fields_as_snake_case_json_to_the_configured_queue()
    {
        SendMessageRequest? captured = null;
        var sqs = new Mock<IAmazonSQS>();
        sqs.Setup(s => s.SendMessageAsync(It.IsAny<SendMessageRequest>(), It.IsAny<CancellationToken>()))
            .Callback<SendMessageRequest, CancellationToken>((req, _) => captured = req)
            .ReturnsAsync(new SendMessageResponse());

        var forwarder = new SqsMessageForwarder(sqs.Object, Options.Create(new SqsOptions { QueueUrl = "https://sqs.example/TransactionQueue-develop" }));

        await forwarder.SendAsync(Sample, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal("https://sqs.example/TransactionQueue-develop", captured!.QueueUrl);
        Assert.Contains("\"transaction_no\":\"TXN-1\"", captured.MessageBody);
        Assert.Contains("\"amount\":\"100.00\"", captured.MessageBody);
        Assert.DoesNotContain("\"id\"", captured.MessageBody); // id/shard_id are TransactionWorker's job, not this service's
    }

    [Fact]
    public async Task SendAsync_propagates_transient_SQS_failures()
    {
        var sqs = new Mock<IAmazonSQS>();
        sqs.Setup(s => s.SendMessageAsync(It.IsAny<SendMessageRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AmazonSQSException("throttled"));

        var forwarder = new SqsMessageForwarder(sqs.Object, Options.Create(new SqsOptions { QueueUrl = "https://sqs.example/TransactionQueue-develop" }));

        await Assert.ThrowsAsync<AmazonSQSException>(() => forwarder.SendAsync(Sample, CancellationToken.None));
    }
}
