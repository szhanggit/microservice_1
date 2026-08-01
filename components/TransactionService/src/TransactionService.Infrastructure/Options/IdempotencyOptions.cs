namespace TransactionService.Infrastructure.Options;

public sealed class IdempotencyOptions
{
    /// <summary>Same default window as the Gateway's (TransactionService.md §4).</summary>
    public TimeSpan EntryTtl { get; set; } = TimeSpan.FromMinutes(15);
}
