using TransactionService.Application;

namespace TransactionService.UnitTests;

public class TransactionValidatorTests
{
    private readonly TransactionValidator _validator = new();

    private static TransactionSubmission Valid() => new(
        TransactionNo: "TXN-1",
        TransactionDatetime: "2026-07-31T12:00:00Z",
        Amount: "100.00",
        Type: "PAYMENT",
        Status: "PENDING",
        Currency: "USD");

    [Fact]
    public void Valid_submission_passes()
    {
        Assert.Null(_validator.Validate(Valid()));
    }

    [Fact]
    public void Missing_transaction_no_is_rejected()
    {
        var result = _validator.Validate(Valid() with { TransactionNo = "" });
        Assert.NotNull(result);
    }

    [Fact]
    public void Missing_transaction_datetime_is_rejected()
    {
        var result = _validator.Validate(Valid() with { TransactionDatetime = "" });
        Assert.NotNull(result);
    }

    [Fact]
    public void Unrecognized_currency_is_rejected()
    {
        var result = _validator.Validate(Valid() with { Currency = "ZZZ" });
        Assert.NotNull(result);
    }

    [Fact]
    public void Unrecognized_type_is_rejected()
    {
        var result = _validator.Validate(Valid() with { Type = "NOT_A_TYPE" });
        Assert.NotNull(result);
    }

    [Fact]
    public void Unrecognized_status_is_rejected()
    {
        var result = _validator.Validate(Valid() with { Status = "NOT_A_STATUS" });
        Assert.NotNull(result);
    }

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("0")]
    [InlineData("-10.00")]
    [InlineData("")]
    public void Invalid_amount_is_rejected(string amount)
    {
        var result = _validator.Validate(Valid() with { Amount = amount });
        Assert.NotNull(result);
    }

    [Fact]
    public void Currency_check_is_case_insensitive()
    {
        Assert.Null(_validator.Validate(Valid() with { Currency = "usd" }));
    }
}
