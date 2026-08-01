namespace TransactionService.Infrastructure.Options;

/// <summary>Read-only connection string for the transactions_reporting instance (database.md §7).</summary>
public sealed class ReportingOptions
{
    public string ConnectionString { get; set; } = string.Empty;
}
