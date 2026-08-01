using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using TransactionWorker.Application;
using TransactionWorker.Infrastructure;
using TransactionWorker.Infrastructure.Options;

namespace TransactionWorker.UnitTests.Infrastructure;

public class StaleClaimReclaimServiceTests
{
    private readonly Mock<IClaimStore> _claimStore = new();
    private readonly Mock<IMessageProcessor> _processor = new();
    private readonly IServiceScopeFactory _scopeFactory;

    public StaleClaimReclaimServiceTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_claimStore.Object);
        services.AddSingleton(_processor.Object);
        _scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private StaleClaimReclaimService CreateService() => new(
        _scopeFactory,
        Options.Create(new StaleClaimReclaimOptions { ScanInterval = TimeSpan.FromSeconds(30) }),
        NullLogger<StaleClaimReclaimService>.Instance);

    [Fact]
    public async Task ScanOnceAsync_does_nothing_when_there_are_no_stale_claims()
    {
        _claimStore.Setup(c => c.FindStaleClaimsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);

        await CreateService().ScanOnceAsync(CancellationToken.None);

        _processor.Verify(p => p.TryResumeStaleClaimAsync(It.IsAny<StaleClaim>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ScanOnceAsync_attempts_to_resume_every_stale_claim_found()
    {
        var claimA = new StaleClaim("TXN-1", "{}", 1000, 1);
        var claimB = new StaleClaim("TXN-2", "{}", 1000, 1);
        _claimStore.Setup(c => c.FindStaleClaimsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([claimA, claimB]);
        _processor.Setup(p => p.TryResumeStaleClaimAsync(claimA, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _processor.Setup(p => p.TryResumeStaleClaimAsync(claimB, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        await CreateService().ScanOnceAsync(CancellationToken.None);

        _processor.Verify(p => p.TryResumeStaleClaimAsync(claimA, It.IsAny<CancellationToken>()), Times.Once);
        _processor.Verify(p => p.TryResumeStaleClaimAsync(claimB, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ScanOnceAsync_swallows_a_scan_failure_without_throwing()
    {
        _claimStore.Setup(c => c.FindStaleClaimsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("dynamodb unavailable"));

        await CreateService().ScanOnceAsync(CancellationToken.None);

        // No exception means success - the periodic scan must survive a transient failure.
    }

    [Fact]
    public async Task ScanOnceAsync_continues_with_remaining_claims_after_one_resume_throws()
    {
        var claimA = new StaleClaim("TXN-1", "{}", 1000, 1);
        var claimB = new StaleClaim("TXN-2", "{}", 1000, 1);
        _claimStore.Setup(c => c.FindStaleClaimsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([claimA, claimB]);
        _processor.Setup(p => p.TryResumeStaleClaimAsync(claimA, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"));
        _processor.Setup(p => p.TryResumeStaleClaimAsync(claimB, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        await CreateService().ScanOnceAsync(CancellationToken.None);

        _processor.Verify(p => p.TryResumeStaleClaimAsync(claimB, It.IsAny<CancellationToken>()), Times.Once);
    }
}
