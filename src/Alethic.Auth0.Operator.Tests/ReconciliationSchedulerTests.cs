using System;
using System.Threading;
using System.Threading.Tasks;

using Alethic.Auth0.Operator.Options;
using Alethic.Auth0.Operator.RateLimiting;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using Moq;

namespace Alethic.Auth0.Operator.Tests
{
    [TestClass]
    public sealed class ReconciliationSchedulerTests
    {
        private Mock<IRateLimiterService> _rateLimiterMock = null!;
        private Mock<ILogger<ReconciliationScheduler>> _loggerMock = null!;
        private IMemoryCache _cache = null!;
        private IOptions<OperatorOptions> _operatorOptions = null!;

        [TestInitialize]
        public void Setup()
        {
            _rateLimiterMock = new Mock<IRateLimiterService>();
            _loggerMock = new Mock<ILogger<ReconciliationScheduler>>();
            _cache = new MemoryCache(new MemoryCacheOptions());
            _operatorOptions = Microsoft.Extensions.Options.Options.Create(new OperatorOptions
            {
                Reconciliation = new ReconciliationOptions
                {
                    EnableStartupSpread = true,
                    StartupSpreadWindow = TimeSpan.FromMinutes(2),
                    MinThrottledInterval = TimeSpan.FromMinutes(5)
                }
            });
        }

        [TestCleanup]
        public void Cleanup()
        {
            _cache?.Dispose();
        }

        [TestMethod]
        public async Task BeginReconcileAsync_FirstReconciliation_ShouldApplyStartupSpreadOrProceed()
        {
            // Arrange
            _rateLimiterMock.Setup(r => r.AcquireAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new RateLimitAcquisition(true, null, "test/tenant"));
            var scheduler = new ReconciliationScheduler(_cache, _rateLimiterMock.Object, _operatorOptions, _loggerMock.Object);

            // Act
            var decision = await scheduler.BeginReconcileAsync("test/entity", "test/tenant");

            // Assert - first reconciliation either defers (startup spread) or proceeds (if hash results in 0 delay)
            // Either way, the entity should be marked as reconciled
            if (decision.ShouldProceed)
            {
                Assert.AreEqual(SchedulingReason.Proceed, decision.Reason);
            }
            else
            {
                Assert.AreEqual(SchedulingReason.StartupSpread, decision.Reason);
                Assert.IsTrue(decision.Delay.HasValue);
                Assert.IsTrue(decision.Delay.Value <= TimeSpan.FromMinutes(2));
            }
            
            // Verify entity is marked as reconciled
            Assert.IsTrue(scheduler.HasReconciledSinceStartup("test/entity"));
        }

        [TestMethod]
        public async Task BeginReconcileAsync_SecondReconciliation_ShouldProceed()
        {
            // Arrange
            _rateLimiterMock.Setup(r => r.AcquireAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new RateLimitAcquisition(true, null, "test/tenant"));
            var scheduler = new ReconciliationScheduler(_cache, _rateLimiterMock.Object, _operatorOptions, _loggerMock.Object);

            // First reconciliation
            await scheduler.BeginReconcileAsync("test/entity", "test/tenant");

            // Act - Second reconciliation
            var decision = await scheduler.BeginReconcileAsync("test/entity", "test/tenant");

            // Assert - should proceed (already reconciled once)
            Assert.IsTrue(decision.ShouldProceed);
            Assert.AreEqual(SchedulingReason.Proceed, decision.Reason);
        }

        [TestMethod]
        public async Task BeginReconcileAsync_WhenRateLimited_ShouldDefer()
        {
            // Arrange
            _rateLimiterMock.Setup(r => r.AcquireAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new RateLimitAcquisition(false, TimeSpan.FromSeconds(30), "test/tenant"));
            
            // Disable startup spread to test rate limit logic
            var options = Microsoft.Extensions.Options.Options.Create(new OperatorOptions
            {
                Reconciliation = new ReconciliationOptions
                {
                    EnableStartupSpread = false,
                    MinThrottledInterval = TimeSpan.FromMinutes(5)
                }
            });
            var scheduler = new ReconciliationScheduler(_cache, _rateLimiterMock.Object, options, _loggerMock.Object);

            // Act
            var decision = await scheduler.BeginReconcileAsync("test/entity", "test/tenant");

            // Assert
            Assert.IsFalse(decision.ShouldProceed);
            Assert.AreEqual(SchedulingReason.ProactiveThrottle, decision.Reason);
            Assert.IsTrue(decision.Delay.HasValue);
            // Should use MinThrottledInterval since it's greater than the retry-after
            Assert.AreEqual(TimeSpan.FromMinutes(5), decision.Delay.Value);
        }

        [TestMethod]
        public void HasReconciledSinceStartup_WhenNotReconciled_ReturnsFalse()
        {
            // Arrange
            var scheduler = new ReconciliationScheduler(_cache, _rateLimiterMock.Object, _operatorOptions, _loggerMock.Object);

            // Act
            var result = scheduler.HasReconciledSinceStartup("new/entity");

            // Assert
            Assert.IsFalse(result);
        }

        [TestMethod]
        public async Task HasReconciledSinceStartup_AfterReconciliation_ReturnsTrue()
        {
            // Arrange
            _rateLimiterMock.Setup(r => r.AcquireAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new RateLimitAcquisition(true, null, "test/tenant"));
            var scheduler = new ReconciliationScheduler(_cache, _rateLimiterMock.Object, _operatorOptions, _loggerMock.Object);

            // First reconciliation (triggers startup spread marking)
            await scheduler.BeginReconcileAsync("test/entity", "test/tenant");

            // Act
            var result = scheduler.HasReconciledSinceStartup("test/entity");

            // Assert
            Assert.IsTrue(result);
        }

        [TestMethod]
        public async Task StartupSpread_SameEntity_GetsSameDelay()
        {
            // Arrange
            var scheduler1 = new ReconciliationScheduler(new MemoryCache(new MemoryCacheOptions()), _rateLimiterMock.Object, _operatorOptions, _loggerMock.Object);
            var scheduler2 = new ReconciliationScheduler(new MemoryCache(new MemoryCacheOptions()), _rateLimiterMock.Object, _operatorOptions, _loggerMock.Object);

            // Act
            var decision1 = await scheduler1.BeginReconcileAsync("test/entity", "test/tenant");
            var decision2 = await scheduler2.BeginReconcileAsync("test/entity", "test/tenant");

            // Assert - Same entity should get same deterministic delay
            Assert.AreEqual(decision1.Delay, decision2.Delay);
        }
    }
}
