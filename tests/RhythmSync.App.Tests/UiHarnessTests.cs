using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using RhythmSync.App.Tests.Ui;

namespace RhythmSync.App.Tests;

/// <summary>The shared UI test harness: failures, hangs and late exceptions reach the test instead of the test host.</summary>
[Collection("Ui")]
public class UiHarnessTests
{
    [Fact]
    public void The_binding_error_recorder_sees_a_broken_binding()
    {
        UiDispatcher.Run(() =>
        {
            using var recorder = new BindingErrorRecorder();
            var text = new TextBlock { DataContext = new object() };
            text.SetBinding(TextBlock.TextProperty, new Binding("NoSuchProperty"));

            UiDispatcher.Render(text);

            Assert.Contains(recorder.Errors, e => e.Contains("NoSuchProperty"));
        });
    }

    [Fact]
    public void The_ui_dispatcher_rethrows_failures_to_the_test()
    {
        var thrown = Assert.Throws<InvalidOperationException>(() => UiDispatcher.Run(() => throw new InvalidOperationException("boom")));
        Assert.Equal("boom", thrown.Message);
    }

    [Fact]
    public void A_body_that_never_finishes_times_out_instead_of_hanging()
    {
        var never = new TaskCompletionSource();
        var clock = Stopwatch.StartNew();
        try
        {
            var thrown = Assert.Throws<TimeoutException>(() => UiDispatcher.Run(() => never.Task.Wait(), TimeSpan.FromSeconds(1)));
            Assert.Contains("1", thrown.Message);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"took {clock.Elapsed}");
        }
        finally
        {
            never.SetResult();   // free the shared UI thread for the tests that follow
        }
    }

    [Fact]
    public void An_exception_from_a_posted_callback_surfaces_in_the_next_run()
    {
        UiDispatcher.Run(() => Dispatcher.CurrentDispatcher.BeginInvoke(() => throw new InvalidOperationException("late")));

        var thrown = Assert.Throws<InvalidOperationException>(() => UiDispatcher.Run(() => { }));
        Assert.Equal("late", thrown.Message);

        UiDispatcher.Run(() => { });   // reported once, then cleared
    }
}
