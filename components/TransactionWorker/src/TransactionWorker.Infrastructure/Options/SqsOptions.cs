namespace TransactionWorker.Infrastructure.Options;

public sealed class SqsOptions
{
    /// <summary>TransactionQueue-<![CDATA[<env>]]> URL (SQS.md).</summary>
    public string QueueUrl { get; set; } = string.Empty;

    public int MaxNumberOfMessages { get; set; } = 10;

    /// <summary>Long-poll wait (KEDA.md §9) rather than tight-loop short polling.</summary>
    public int WaitTimeSeconds { get; set; } = 20;
}
