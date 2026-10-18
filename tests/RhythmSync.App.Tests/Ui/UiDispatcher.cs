using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Threading;
using RhythmSync.App.Controls;

namespace RhythmSync.App.Tests.Ui;

/// <summary>
/// One shared STA thread that hosts a plain <see cref="Application"/> carrying the app's resources
/// (Themes/AppResources.xaml: converters, shared styles and the Studio theme), so tests can build and lay out real
/// views without showing a window.
/// SAFETY: this must never construct the real RhythmSync App. Running the dispatcher raises Application.Startup,
/// which would run App.OnStartup: real logging, a tray icon, sockets and the elevated netsh firewall setup.
/// </summary>
internal static class UiDispatcher
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private static readonly Lazy<Dispatcher> Shared = new(Start, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly object UnhandledGate = new();
    private static ExceptionDispatchInfo? s_unhandled;

    /// <summary>
    /// The dictionary App.xaml merges (converters, shared styles, and the Studio theme), addressed from outside the
    /// app assembly (whose name is "RhythmSync").
    /// Built on demand: the pack scheme is only registered once WPF's Application type is initialised.
    /// </summary>
    public static Uri AppResourcesUri => new(
        $"pack://application:,,,/{typeof(SyncStateToKindConverter).Assembly.GetName().Name};component/Themes/AppResources.xaml");

    /// <summary>
    /// Runs <paramref name="body"/> on the UI thread and rethrows anything it throws, with its original stack.
    /// Throws <see cref="TimeoutException"/> if it has not finished within <paramref name="timeout"/> (default 30 s),
    /// and rethrows an exception that an earlier posted callback left unhandled on the UI thread.
    /// </summary>
    public static void Run(Action body, TimeSpan? timeout = null)
    {
        var limit = timeout ?? DefaultTimeout;
        var dispatcher = Shared.Value;
        RethrowUnhandled();

        ExceptionDispatchInfo? failure = null;
        var operation = dispatcher.InvokeAsync(() =>
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
        if (!operation.Task.Wait(limit))
            throw new TimeoutException($"The UI test body did not finish within {limit.TotalSeconds:0.###} s.");

        failure?.Throw();
        RethrowUnhandled();
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

    /// <summary>Reports (once) the first exception a posted callback threw on the UI thread since the last check.</summary>
    private static void RethrowUnhandled()
    {
        ExceptionDispatchInfo? pending;
        lock (UnhandledGate)
        {
            pending = s_unhandled;
            s_unhandled = null;
        }
        pending?.Throw();
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;   // keep the shared thread, and the test host, alive
        lock (UnhandledGate)
            s_unhandled ??= ExceptionDispatchInfo.Capture(e.Exception);
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
                dispatcher.UnhandledException += OnUnhandledException;
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = AppResourcesUri });
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
