namespace TransactionService.Infrastructure.Options;

public sealed class SqsOptions
{
    /// <summary>TransactionQueue-<![CDATA[<env>]]> URL (SQS.md).</summary>
    public string QueueUrl { get; set; } = string.Empty;
}
