using TransactionWorker.Application;

namespace TransactionWorker.Infrastructure;

/// <summary>
/// Derives this instance's identity from its pod ordinal/hostname (database.md
/// §5, KEDA.md §5) - the WORKER_ID env var wins if set (useful when running
/// multiple docker-compose replicas that might otherwise share a hostname),
/// falling back to the machine's own hostname, which is already unique per
/// pod in Kubernetes.
/// </summary>
public sealed class HostnameWorkerIdentity : IWorkerIdentity
{
    public string WorkerId { get; }

    public HostnameWorkerIdentity()
    {
        WorkerId = Environment.GetEnvironmentVariable("WORKER_ID") ?? Environment.MachineName;
    }
}
