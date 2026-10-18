using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Threading;

namespace RhythmSync.App.Tests.Ui;

/// <summary>
/// One shared STA thread that owns a WPF <see cref="Application"/> with the Studio theme merged,
/// so tests can build and lay out real controls without showing a window.
/// </summary>
internal static class UiDispatcher
{
    private static readonly Lazy<Dispatcher> Shared = new(Start, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// The theme the app merges in App.xaml, addressed from outside the app assembly (whose name is "RhythmSync").
    /// Built on demand: the pack scheme is only registered once WPF's Application type is initialised.
    /// </summary>
    public static Uri ThemeUri => new(
        $"pack://application:,,,/{typeof(App).Assembly.GetName().Name};component/Themes/Studio.xaml");

    /// <summary>Runs <paramref name="body"/> on the UI thread and rethrows anything it throws, with its original stack.</summary>
    public static void Run(Action body)
    {
        ExceptionDispatchInfo? failure = null;
        Shared.Value.Invoke(() =>
        {
            try
            {
                body();
            }
            catch (Exception e)
            {
                failure = ExceptionDispatchInfo.Capture(e);
            }
        });
        failure?.Throw();
    }

    /// <summary>Sizes and lays out <paramref name="element"/>, then lets the dispatcher run until idle (bindings, templates, layout). Call inside <see cref="Run"/>.</summary>
    public static void Render(FrameworkElement element, double width = 960, double height = 640)
    {
        element.Width = width;
        element.Height = height;
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
        element.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static Dispatcher Start()
    {
        using var ready = new ManualResetEventSlim();
        Dispatcher? dispatcher = null;
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                dispatcher = Dispatcher.CurrentDispatcher;
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = ThemeUri });
            }
            catch (Exception e)
            {
                failure = e;
            }
            ready.Set();
            if (failure is null)
                Dispatcher.Run();
        })
        {
            IsBackground = true,   // lets the test host exit without shutting the dispatcher down
            Name = "UI test dispatcher",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();

        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
        return dispatcher!;
    }
}
