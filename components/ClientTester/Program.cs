using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using ClientTester;
using Microsoft.Extensions.Configuration;

var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddEnvironmentVariables()
    .AddCommandLine(args)
    .Build();

var section = configuration.GetSection("ClientTester");
var targetUrl = section["TargetUrl"] ?? "https://microservice1.ekslab.xyz/api/v1/transactions";
var concurrency = int.TryParse(section["Concurrency"], out var parsedConcurrency) ? parsedConcurrency : 50;
var reportInterval = TimeSpan.FromSeconds(
    int.TryParse(section["ReportIntervalSeconds"], out var parsedInterval) ? parsedInterval : 2);

Console.WriteLine($"ClientTester: target={targetUrl} concurrency={concurrency} (Ctrl+C to stop)");
Console.WriteLine();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    // Without this, the default SIGINT behavior kills the process before
    // the workers/reporter below get a chance to print a final summary.
    e.Cancel = true;
    cts.Cancel();
};

// PooledConnectionLifetime bounds how long any one TCP connection is reused.
// Left at the default (unbounded), SocketsHttpHandler would keep reusing
// the same `concurrency` sockets for the entire run, which under an ALB
// means load stays pinned to whichever backend pods those first connections
// happened to land on instead of spreading across the target group over
// time.
var handler = new SocketsHttpHandler
{
    PooledConnectionLifetime = TimeSpan.FromSeconds(60)
};
using var httpClient = new HttpClient(handler)
{
    Timeout = TimeSpan.FromSeconds(30)
};
httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

var stats = new RequestStats();
stats.NewErrorReasonSeen += reason => Console.WriteLine($"[error] first occurrence: {reason}");
var payloadFactory = new PayloadFactory();

var workers = Enumerable.Range(0, concurrency)
    .Select(_ => RunWorkerAsync(httpClient, targetUrl, payloadFactory, stats, cts.Token));
var reporter = RunReporterAsync(stats, reportInterval, cts.Token);

try
{
    await Task.WhenAll(workers.Append(reporter));
}
catch (OperationCanceledException)
{
    // Expected on Ctrl+C.
}

var totals = stats.TotalsSnapshot();
Console.WriteLine();
Console.WriteLine($"Final: sent={totals.Sent} ok={totals.Success} err={totals.Error}");
if (totals.Error > 0)
{
    Console.WriteLine("Error breakdown:");
    foreach (var (reason, count) in stats.ErrorReasonsSnapshot())
    {
        Console.WriteLine($"  {count,8}  {reason}");
    }
}

return;

static async Task RunWorkerAsync(
    HttpClient client, string url, PayloadFactory payloadFactory, RequestStats stats, CancellationToken token)
{
    var stopwatch = new Stopwatch();
    while (!token.IsCancellationRequested)
    {
        using var content = new StringContent(payloadFactory.NextPayload(), Encoding.UTF8, "application/json");
        stopwatch.Restart();
        try
        {
            using var response = await client.PostAsync(url, content, token);
            if (response.IsSuccessStatusCode)
            {
                stats.RecordSuccess(stopwatch.Elapsed.TotalMilliseconds);
            }
            else
            {
                stats.RecordFailure(stopwatch.Elapsed.TotalMilliseconds, $"HTTP {(int)response.StatusCode} {response.StatusCode}");
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            break;
        }
        catch (Exception ex)
        {
            // Network errors/timeouts count as failed requests, not a
            // reason to stop this worker - a real load test should keep
            // going and let the live summary show the error rate. The
            // innermost exception (e.g. SocketException under an
            // HttpRequestException) is usually the actually useful part -
            // "Connection refused" vs. just "An error occurred while
            // sending the request".
            var innermost = ex;
            while (innermost.InnerException is not null)
            {
                innermost = innermost.InnerException;
            }

            stats.RecordFailure(stopwatch.Elapsed.TotalMilliseconds, $"{innermost.GetType().Name}: {innermost.Message}");
        }
    }
}

static async Task RunReporterAsync(RequestStats stats, TimeSpan interval, CancellationToken token)
{
    var elapsed = Stopwatch.StartNew();
    try
    {
        while (true)
        {
            await Task.Delay(interval, token);
            var snapshot = stats.TakeIntervalSnapshot();
            var totals = stats.TotalsSnapshot();
            var rate = snapshot.Count / interval.TotalSeconds;
            Console.WriteLine(
                $"[{elapsed.Elapsed:hh\\:mm\\:ss}] sent={totals.Sent} ({rate:F0}/s) ok={totals.Success} err={totals.Error} avg={snapshot.AvgMs:F0}ms p95={snapshot.P95Ms:F0}ms");
        }
    }
    catch (OperationCanceledException)
    {
        // Expected on Ctrl+C.
    }
}
