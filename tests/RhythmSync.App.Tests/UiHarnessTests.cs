using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using RhythmSync.App.Tests.Ui;

namespace RhythmSync.App.Tests;

/// <summary>The shared UI test harness: failures, hangs and late exceptions reach the test instead of the test host.</summary>
[Collection("Ui")]
public class UiHarnessTests
{
    [Fact]
    public void The_harness_never_hosts_the_real_app()
    {
        // The real App's OnStartup starts logging, the tray icon, sockets and the elevated netsh firewall setup.
        UiDispatcher.Run(() =>
        {
            Assert.NotNull(Application.Current);
            Assert.False(Application.Current is RhythmSync.App.App, "the harness constructed the real RhythmSync App");
            Assert.Equal(typeof(Application), Application.Current.GetType());
        });
    }

    [Fact]
    public void The_app_resources_resolve_from_the_plain_application()
    {
        UiDispatcher.Run(() =>
        {
            var resources = Application.Current.Resources;
            Assert.IsType<BooleanToVisibilityConverter>(resources["BoolToVisibility"]);
            Assert.Equal(typeof(TextBlock), Assert.IsType<Style>(resources["OptionalText"]).TargetType);
            Assert.IsType<SolidColorBrush>(Application.Current.FindResource("AccentBrush"));
        });
    }

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
    public void An_exception_from_a_callback_the_body_posted_surfaces_in_the_same_run()
    {
        // Run drains what the body queued before returning, so this is reported here, never by chance in the next Run.
        var thrown = Assert.Throws<InvalidOperationException>(() =>
            UiDispatcher.Run(() => Dispatcher.CurrentDispatcher.BeginInvoke(() => throw new InvalidOperationException("late"))));
        Assert.Equal("late", thrown.Message);

        UiDispatcher.Run(() => { });   // reported once, then cleared
    }

    [Fact]
    public void An_exception_from_a_callback_posted_between_runs_surfaces_in_the_next_run()
    {
        Dispatcher? dispatcher = null;
        UiDispatcher.Run(() => dispatcher = Dispatcher.CurrentDispatcher);

        // Posted from the test thread, as an app event marshalled to the UI would be; the next Run's body queues behind it.
        dispatcher!.BeginInvoke(() => throw new InvalidOperationException("between"));

        var thrown = Assert.Throws<InvalidOperationException>(() => UiDispatcher.Run(() => { }));
        Assert.Equal("between", thrown.Message);

        UiDispatcher.Run(() => { });   // reported once, then cleared
    }
}
