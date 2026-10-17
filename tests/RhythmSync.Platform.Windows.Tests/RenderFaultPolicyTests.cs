using System.Runtime.InteropServices;
using RhythmSync.Platform.Windows;

namespace RhythmSync.Platform.Windows.Tests;

public class RenderFaultPolicyTests
{
    [Theory]
    [InlineData(0x88890004u)]   // AUDCLNT_E_DEVICE_INVALIDATED
    [InlineData(0x88890026u)]   // AUDCLNT_E_RESOURCES_INVALIDATED
    [InlineData(0x88890010u)]   // AUDCLNT_E_SERVICE_NOT_RUNNING
    [InlineData(0x80070490u)]   // ERROR_NOT_FOUND
    public void Device_gone_hresults_invalidate_immediately(uint hr)
    {
        var policy = new RenderFaultPolicy();
        Assert.True(policy.ShouldInvalidate(new COMException("gone", unchecked((int)hr))));
    }

    [Fact]
    public void Other_errors_invalidate_only_on_the_twentieth_consecutive_failure()
    {
        var policy = new RenderFaultPolicy();
        for (int i = 1; i < RenderFaultPolicy.MaxConsecutiveErrors; i++)
            Assert.False(policy.ShouldInvalidate(new COMException("other", unchecked((int)0x80004005))));
        Assert.True(policy.ShouldInvalidate(new InvalidOperationException()));
    }

    [Fact]
    public void A_success_resets_the_consecutive_count()
    {
        var policy = new RenderFaultPolicy();
        for (int round = 0; round < 5; round++)
        {
            for (int i = 0; i < RenderFaultPolicy.MaxConsecutiveErrors - 1; i++)
                Assert.False(policy.ShouldInvalidate(new InvalidOperationException()));
            policy.Succeeded();
        }
    }

    [Fact]
    public void Invalidation_can_be_claimed_exactly_once_even_concurrently()
    {
        var policy = new RenderFaultPolicy();
        int winners = 0;
        Parallel.For(0, 64, _ => { if (policy.TryClaimInvalidation()) Interlocked.Increment(ref winners); });
        Assert.Equal(1, winners);
        Assert.False(policy.TryClaimInvalidation());
    }
}
