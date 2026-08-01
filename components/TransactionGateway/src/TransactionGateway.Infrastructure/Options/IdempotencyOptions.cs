namespace TransactionGateway.Infrastructure.Options;

public sealed class IdempotencyOptions
{
    /// <summary>
    /// TransactionGateway.md §4: 15 minutes - long enough to cover a realistic
    /// client-retry window, short enough not to matter for storage.
    /// </summary>
    public TimeSpan EntryTtl { get; set; } = TimeSpan.FromMinutes(15);
}
