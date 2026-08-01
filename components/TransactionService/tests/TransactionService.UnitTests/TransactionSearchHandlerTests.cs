using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ShardRouting;
using TransactionService.Application;

namespace TransactionService.UnitTests;

public class TransactionSearchHandlerTests
{
    private static readonly TransactionRecord SampleRecord = new(
        Id: 123456789L,
        TransactionNo: "TXN-1",
        TransactionDatetime: "2026-07-31T12:00:00Z",
        Amount: "100.00",
        Type: "PAYMENT",
        Status: "PENDING",
        Currency: "USD",
        SystemDatetime: "2026-07-31T12:00:01Z");

    private readonly Mock<IShardRouter> _shardRouter = new();
    private readonly Mock<ITransactionSearchRepository> _repository = new();

    private TransactionSearchHandler CreateHandler() =>
        new(_shardRouter.Object, _repository.Object, NullLogger<TransactionSearchHandler>.Instance);

    [Fact]
    public async Task SearchByTransactionNoAsync_rejects_an_empty_transaction_no_without_touching_the_repository()
    {
        var result = await CreateHandler().SearchByTransactionNoAsync(string.Empty, CancellationToken.None);

        Assert.Equal(TransactionNoSearchOutcome.InvalidArgument, result.Outcome);
        _repository.Verify(r => r.FindByTransactionNoAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SearchByTransactionNoAsync_returns_Found_for_an_existing_row()
    {
        _shardRouter.Setup(r => r.ResolveShardId("TXN-1")).Returns(2);
        _repository.Setup(r => r.FindByTransactionNoAsync("TXN-1", 2, It.IsAny<CancellationToken>())).ReturnsAsync(SampleRecord);

        var result = await CreateHandler().SearchByTransactionNoAsync("TXN-1", CancellationToken.None);

        Assert.Equal(TransactionNoSearchOutcome.Found, result.Outcome);
        Assert.Equal(SampleRecord, result.Transaction);
    }

    [Fact]
    public async Task SearchByTransactionNoAsync_returns_NotFound_not_an_error_for_a_search_miss()
    {
        _shardRouter.Setup(r => r.ResolveShardId("TXN-1")).Returns(2);
        _repository.Setup(r => r.FindByTransactionNoAsync("TXN-1", 2, It.IsAny<CancellationToken>())).ReturnsAsync((TransactionRecord?)null);

        var result = await CreateHandler().SearchByTransactionNoAsync("TXN-1", CancellationToken.None);

        Assert.Equal(TransactionNoSearchOutcome.NotFound, result.Outcome);
        Assert.Null(result.Transaction);
    }

    [Fact]
    public async Task SearchByTransactionNoAsync_routes_to_the_shard_resolved_by_the_shared_router()
    {
        _shardRouter.Setup(r => r.ResolveShardId("TXN-1")).Returns(1);
        _repository.Setup(r => r.FindByTransactionNoAsync("TXN-1", 1, It.IsAny<CancellationToken>())).ReturnsAsync(SampleRecord);

        await CreateHandler().SearchByTransactionNoAsync("TXN-1", CancellationToken.None);

        _repository.Verify(r => r.FindByTransactionNoAsync("TXN-1", 1, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("not-a-date", "2026-07-31T12:00:00Z")]
    [InlineData("2026-07-01T00:00:00Z", "not-a-date")]
    public async Task SearchByDateRangeAsync_rejects_unparseable_dates(string from, string to)
    {
        var result = await CreateHandler().SearchByDateRangeAsync(from, to, limit: 50, offset: 0, CancellationToken.None);

        Assert.Equal(DateRangeSearchOutcome.InvalidArgument, result.Outcome);
        _repository.Verify(
            r => r.FindByDateRangeAsync(It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SearchByDateRangeAsync_rejects_from_after_to()
    {
        var result = await CreateHandler().SearchByDateRangeAsync("2026-08-01T00:00:00Z", "2026-07-01T00:00:00Z", limit: 50, offset: 0, CancellationToken.None);

        Assert.Equal(DateRangeSearchOutcome.InvalidArgument, result.Outcome);
    }

    [Fact]
    public async Task SearchByDateRangeAsync_rejects_a_negative_offset()
    {
        var result = await CreateHandler().SearchByDateRangeAsync("2026-07-01T00:00:00Z", "2026-07-31T00:00:00Z", limit: 50, offset: -1, CancellationToken.None);

        Assert.Equal(DateRangeSearchOutcome.InvalidArgument, result.Outcome);
    }

    [Fact]
    public async Task SearchByDateRangeAsync_defaults_a_non_positive_limit_to_50()
    {
        _repository
            .Setup(r => r.FindByDateRangeAsync(It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), 50, 0, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TransactionSearchPage([SampleRecord], HasMore: false));

        var result = await CreateHandler().SearchByDateRangeAsync("2026-07-01T00:00:00Z", "2026-07-31T00:00:00Z", limit: 0, offset: 0, CancellationToken.None);

        Assert.Equal(DateRangeSearchOutcome.Success, result.Outcome);
        _repository.Verify(
            r => r.FindByDateRangeAsync(It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), 50, 0, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SearchByDateRangeAsync_clamps_a_limit_above_500_down_to_500()
    {
        _repository
            .Setup(r => r.FindByDateRangeAsync(It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), 500, 0, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TransactionSearchPage([], HasMore: false));

        await CreateHandler().SearchByDateRangeAsync("2026-07-01T00:00:00Z", "2026-07-31T00:00:00Z", limit: 10_000, offset: 0, CancellationToken.None);

        _repository.Verify(
            r => r.FindByDateRangeAsync(It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), 500, 0, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SearchByDateRangeAsync_returns_the_repository_page_on_the_happy_path()
    {
        var page = new TransactionSearchPage([SampleRecord], HasMore: true);
        _repository
            .Setup(r => r.FindByDateRangeAsync(It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), 50, 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(page);

        var result = await CreateHandler().SearchByDateRangeAsync("2026-07-01T00:00:00Z", "2026-07-31T00:00:00Z", limit: 50, offset: 10, CancellationToken.None);

        Assert.Equal(DateRangeSearchOutcome.Success, result.Outcome);
        Assert.Equal(page.Items, result.Transactions);
        Assert.True(result.HasMore);
    }
}
