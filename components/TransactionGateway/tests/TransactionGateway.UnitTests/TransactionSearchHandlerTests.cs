using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TransactionGateway.Application;

namespace TransactionGateway.UnitTests;

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

    private readonly Mock<ITransactionSearchForwarder> _forwarder = new();

    private TransactionSearchHandler CreateHandler() =>
        new(_forwarder.Object, NullLogger<TransactionSearchHandler>.Instance);

    [Fact]
    public async Task SearchByTransactionNoAsync_rejects_an_empty_transaction_no_without_calling_the_forwarder()
    {
        var result = await CreateHandler().SearchByTransactionNoAsync(string.Empty, CancellationToken.None);

        Assert.Equal(TransactionNoSearchOutcome.InvalidArgument, result.Outcome);
        _forwarder.Verify(f => f.SearchByTransactionNoAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SearchByTransactionNoAsync_returns_Found_when_the_forwarder_finds_a_row()
    {
        _forwarder.Setup(f => f.SearchByTransactionNoAsync("TXN-1", It.IsAny<CancellationToken>())).ReturnsAsync(SampleRecord);

        var result = await CreateHandler().SearchByTransactionNoAsync("TXN-1", CancellationToken.None);

        Assert.Equal(TransactionNoSearchOutcome.Found, result.Outcome);
        Assert.Equal(SampleRecord, result.Transaction);
    }

    [Fact]
    public async Task SearchByTransactionNoAsync_returns_NotFound_not_an_error_for_a_search_miss()
    {
        _forwarder.Setup(f => f.SearchByTransactionNoAsync("TXN-1", It.IsAny<CancellationToken>())).ReturnsAsync((TransactionRecord?)null);

        var result = await CreateHandler().SearchByTransactionNoAsync("TXN-1", CancellationToken.None);

        Assert.Equal(TransactionNoSearchOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task SearchByTransactionNoAsync_maps_an_InvalidArgument_RpcException_to_InvalidArgument()
    {
        _forwarder.Setup(f => f.SearchByTransactionNoAsync("TXN-1", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RpcException(new Status(StatusCode.InvalidArgument, "transaction_no is required")));

        var result = await CreateHandler().SearchByTransactionNoAsync("TXN-1", CancellationToken.None);

        Assert.Equal(TransactionNoSearchOutcome.InvalidArgument, result.Outcome);
        Assert.Equal("transaction_no is required", result.ErrorMessage);
    }

    [Fact]
    public async Task SearchByTransactionNoAsync_maps_any_other_failure_to_ForwardingFailed()
    {
        _forwarder.Setup(f => f.SearchByTransactionNoAsync("TXN-1", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RpcException(new Status(StatusCode.Unavailable, "connection lost")));

        var result = await CreateHandler().SearchByTransactionNoAsync("TXN-1", CancellationToken.None);

        Assert.Equal(TransactionNoSearchOutcome.ForwardingFailed, result.Outcome);
    }

    [Theory]
    [InlineData("not-a-date", "2026-07-31T12:00:00Z")]
    [InlineData("2026-07-01T00:00:00Z", "not-a-date")]
    public async Task SearchByDateRangeAsync_rejects_unparseable_dates(string from, string to)
    {
        var result = await CreateHandler().SearchByDateRangeAsync(from, to, limit: null, offset: null, CancellationToken.None);

        Assert.Equal(DateRangeSearchOutcome.InvalidArgument, result.Outcome);
        _forwarder.Verify(
            f => f.SearchByDateRangeAsync(It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SearchByDateRangeAsync_rejects_from_after_to()
    {
        var result = await CreateHandler().SearchByDateRangeAsync("2026-08-01T00:00:00Z", "2026-07-01T00:00:00Z", limit: null, offset: null, CancellationToken.None);

        Assert.Equal(DateRangeSearchOutcome.InvalidArgument, result.Outcome);
    }

    [Fact]
    public async Task SearchByDateRangeAsync_rejects_a_negative_offset()
    {
        var result = await CreateHandler().SearchByDateRangeAsync("2026-07-01T00:00:00Z", "2026-07-31T00:00:00Z", limit: null, offset: -1, CancellationToken.None);

        Assert.Equal(DateRangeSearchOutcome.InvalidArgument, result.Outcome);
    }

    [Fact]
    public async Task SearchByDateRangeAsync_defaults_a_missing_limit_to_50_and_offset_to_0()
    {
        _forwarder
            .Setup(f => f.SearchByDateRangeAsync(It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), 50, 0, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TransactionSearchPage([SampleRecord], HasMore: false));

        var result = await CreateHandler().SearchByDateRangeAsync("2026-07-01T00:00:00Z", "2026-07-31T00:00:00Z", limit: null, offset: null, CancellationToken.None);

        Assert.Equal(DateRangeSearchOutcome.Success, result.Outcome);
        _forwarder.Verify(
            f => f.SearchByDateRangeAsync(It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), 50, 0, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SearchByDateRangeAsync_clamps_a_limit_above_500_down_to_500()
    {
        _forwarder
            .Setup(f => f.SearchByDateRangeAsync(It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), 500, 0, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TransactionSearchPage([], HasMore: false));

        await CreateHandler().SearchByDateRangeAsync("2026-07-01T00:00:00Z", "2026-07-31T00:00:00Z", limit: 10_000, offset: null, CancellationToken.None);

        _forwarder.Verify(
            f => f.SearchByDateRangeAsync(It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), 500, 0, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SearchByDateRangeAsync_returns_the_forwarder_page_on_the_happy_path()
    {
        var page = new TransactionSearchPage([SampleRecord], HasMore: true);
        _forwarder
            .Setup(f => f.SearchByDateRangeAsync(It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), 50, 10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(page);

        var result = await CreateHandler().SearchByDateRangeAsync("2026-07-01T00:00:00Z", "2026-07-31T00:00:00Z", limit: 50, offset: 10, CancellationToken.None);

        Assert.Equal(DateRangeSearchOutcome.Success, result.Outcome);
        Assert.Equal(page.Items, result.Transactions);
        Assert.True(result.HasMore);
    }

    [Fact]
    public async Task SearchByDateRangeAsync_maps_an_InvalidArgument_RpcException_from_TransactionService()
    {
        _forwarder
            .Setup(f => f.SearchByDateRangeAsync(It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RpcException(new Status(StatusCode.InvalidArgument, "from must not be after to")));

        var result = await CreateHandler().SearchByDateRangeAsync("2026-07-01T00:00:00Z", "2026-07-31T00:00:00Z", limit: null, offset: null, CancellationToken.None);

        Assert.Equal(DateRangeSearchOutcome.InvalidArgument, result.Outcome);
    }

    [Fact]
    public async Task SearchByDateRangeAsync_maps_any_other_failure_to_ForwardingFailed()
    {
        _forwarder
            .Setup(f => f.SearchByDateRangeAsync(It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var result = await CreateHandler().SearchByDateRangeAsync("2026-07-01T00:00:00Z", "2026-07-31T00:00:00Z", limit: null, offset: null, CancellationToken.None);

        Assert.Equal(DateRangeSearchOutcome.ForwardingFailed, result.Outcome);
    }
}
