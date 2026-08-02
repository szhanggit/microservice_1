using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ShardRouting;
using TransactionWorker.Application;
using TransactionWorker.Domain;

namespace TransactionWorker.UnitTests.Application;

public class MessageProcessorTests
{
    private const string TransactionNo = "TXN-1";

    private const string PayloadJson =
        """{"transaction_no":"TXN-1","transaction_datetime":"2026-07-31T12:00:00Z","amount":"100.00","type":"PAYMENT","status":"PENDING","currency":"USD"}""";

    private readonly Mock<IClaimStore> _claimStore = new();
    private readonly Mock<ITransactionRepository> _repository = new();
    private readonly Mock<IShardRouter> _shardRouter = new();
    private readonly Mock<ISnowflakeIdGenerator> _idGenerator = new();
    private readonly Mock<IWorkerIdentity> _workerIdentity = new();

    public MessageProcessorTests()
    {
        _workerIdentity.Setup(w => w.WorkerId).Returns("worker-1");
        _shardRouter.Setup(r => r.ResolveShardId(TransactionNo)).Returns(2);
        _idGenerator.Setup(g => g.NextId(2)).Returns(123456789L);
    }

    private MessageProcessor CreateProcessor() => new(
        _claimStore.Object,
        _repository.Object,
        _shardRouter.Object,
        _idGenerator.Object,
        _workerIdentity.Object,
        NullLogger<MessageProcessor>.Instance);

    [Fact]
    public async Task ClaimNewMessageAsync_returns_Claimed_for_a_fresh_message()
    {
        _claimStore.Setup(c => c.TryClaimAsync(TransactionNo, "worker-1", PayloadJson, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ClaimOutcome.Claimed);

        var outcome = await CreateProcessor().ClaimNewMessageAsync(TransactionNo, PayloadJson, CancellationToken.None);

        Assert.Equal(ClaimOutcome.Claimed, outcome);
    }

    [Fact]
    public async Task ClaimNewMessageAsync_returns_AlreadyClaimed_for_a_duplicate_delivery()
    {
        _claimStore.Setup(c => c.TryClaimAsync(TransactionNo, "worker-1", PayloadJson, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ClaimOutcome.AlreadyClaimed);

        var outcome = await CreateProcessor().ClaimNewMessageAsync(TransactionNo, PayloadJson, CancellationToken.None);

        Assert.Equal(ClaimOutcome.AlreadyClaimed, outcome);
    }

    [Fact]
    public async Task CompleteProcessingAsync_resolves_shard_generates_id_inserts_and_marks_completed()
    {
        _repository.Setup(r => r.InsertAsync(It.IsAny<TransactionRecord>(), 2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(InsertOutcome.Inserted);

        await CreateProcessor().CompleteProcessingAsync(TransactionNo, PayloadJson, CancellationToken.None);

        _repository.Verify(r => r.InsertAsync(
            It.Is<TransactionRecord>(rec =>
                rec.Id == 123456789L &&
                rec.TransactionNo == TransactionNo &&
                rec.Amount == "100.00" &&
                rec.Type == "PAYMENT" &&
                rec.Status == "PENDING" &&
                rec.Currency == "USD"),
            2,
            It.IsAny<CancellationToken>()),
            Times.Once);
        _claimStore.Verify(c => c.CompleteAsync(TransactionNo, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CompleteProcessingAsync_treats_a_duplicate_key_insert_as_success_and_still_completes()
    {
        // KEDA.md §5's defense-in-depth: a Postgres unique-violation on insert
        // means "already processed", not an error - the claim still gets
        // marked COMPLETED so it doesn't linger as a stale CLAIMED item.
        _repository.Setup(r => r.InsertAsync(It.IsAny<TransactionRecord>(), 2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(InsertOutcome.AlreadyExists);

        await CreateProcessor().CompleteProcessingAsync(TransactionNo, PayloadJson, CancellationToken.None);

        _claimStore.Verify(c => c.CompleteAsync(TransactionNo, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TryResumeStaleClaimAsync_returns_false_and_does_not_touch_the_DB_when_the_reclaim_race_is_lost()
    {
        var claim = new StaleClaim(TransactionNo, PayloadJson, OldLeaseExpiry: 1000, AttemptCount: 1);
        _claimStore.Setup(c => c.TryReclaimAsync(claim, "worker-1", It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var won = await CreateProcessor().TryResumeStaleClaimAsync(claim, CancellationToken.None);

        Assert.False(won);
        _repository.Verify(r => r.InsertAsync(It.IsAny<TransactionRecord>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        _claimStore.Verify(c => c.CompleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TryResumeStaleClaimAsync_wins_the_race_and_resumes_from_the_stored_payload()
    {
        var claim = new StaleClaim(TransactionNo, PayloadJson, OldLeaseExpiry: 1000, AttemptCount: 1);
        _claimStore.Setup(c => c.TryReclaimAsync(claim, "worker-1", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _repository.Setup(r => r.InsertAsync(It.IsAny<TransactionRecord>(), 2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(InsertOutcome.Inserted);

        var won = await CreateProcessor().TryResumeStaleClaimAsync(claim, CancellationToken.None);

        Assert.True(won);
        _repository.Verify(r => r.InsertAsync(
            It.Is<TransactionRecord>(rec => rec.TransactionNo == TransactionNo && rec.Id == 123456789L),
            2,
            It.IsAny<CancellationToken>()),
            Times.Once);
        _claimStore.Verify(c => c.CompleteAsync(TransactionNo, It.IsAny<CancellationToken>()), Times.Once);
    }
}
