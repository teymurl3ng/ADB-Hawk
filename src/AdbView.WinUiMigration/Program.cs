namespace AdbView.WinUiMigration;

using AdbView.WinUiMigration.Host;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Serilog;

internal static class Program {
    private static App? _application;
    private static CandidateWindow? _window;

    [STAThread]
    private static int Main(string[] arguments) {
        bool ownsFixtureLog = false;
        try {
            CandidateOptions options = CandidateOptions.Parse(arguments);
            void Stage(string value) {
                if (options.ResultPath is not null) {
                    File.AppendAllText(options.ResultPath + ".startup.log", value + Environment.NewLine);
                }
            }
            Stage("options parsed");
            if (options.Live) {
                // M08 live-composition slice: an isolated log beside this nonshipping executable;
                // production log/preference paths stay untouched.
                string logPath = Path.Combine(AppContext.BaseDirectory, "candidate-live.log");
                Log.Logger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.File(logPath,
                    shared: true, formatProvider: System.Globalization.CultureInfo.InvariantCulture).CreateLogger();
                ownsFixtureLog = true;
                Log.Information("Live candidate starting with isolated identity {Identity}", options.InstanceName);
            }
            else {
                CandidateWindow.ValidateFixture(options.Fixture);
                if (options.Fixture is "native-scenarios" or "native-bubble") {
                    string logPath = options.ResultPath is { } resultPath ? resultPath + ".native.log"
                        : Path.Combine(AppContext.BaseDirectory, "candidate-" + options.InstanceSuffix + ".native.log");
                    Log.Logger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.File(logPath,
                        formatProvider: System.Globalization.CultureInfo.InvariantCulture).CreateLogger();
                    ownsFixtureLog = true;
                    Log.Information("Native candidate fixture started with isolated instance {Instance}", options.InstanceName);
                }
            }
            using var instance = new Mutex(true, options.InstanceName, out bool created);
            if (!created) {
                if (options.Live) {
                    // Single-instance forwarding is a separate M08 item: refuse and report, never
                    // hijack another live candidate or the production instance.
                    Log.Information("Live candidate: identity {Identity} is already owned; exiting without forwarding.", options.InstanceName);
                    Console.Error.WriteLine("Another live candidate owns the identity; exiting (forwarding is pending M08 work).");
                    return 0;
                }
                throw new InvalidOperationException("Candidate fixture identity is already owned; no forwarding is permitted.");
            }
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Stage("ComWrappers initialized");
            Application.Start(parameters => {
                Stage("Application start callback");
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
                _application = new App();
                _application.UnhandledException += (sender, args) => {
                    Console.Error.WriteLine("WinUI candidate UI failure: " + args.Exception);
                    Environment.Exit(1);
                };
                _application.InitializeComponent();
                Stage("Application XAML initialized");
                if (!DispatcherQueue.GetForCurrentThread().TryEnqueue(() => {
                    Stage("constructing candidate");
                    _window = new CandidateWindow(options);
                    Stage("candidate constructed");
                    if (options.Live) {
                        // Tray-only start: the flyout opens on the tray's Open/toggle, matching
                        // the product shape and stealing no foreground at launch.
                        _window.StartLiveBackend();
                        Log.Information("Live candidate ready (tray-only; flyout opens from the tray).");
                    }
                    else {
                        _window.ShowSurface();
                        Stage("candidate activated");
                    }
                })) {
                    throw new InvalidOperationException("Candidate startup dispatch was rejected.");
                }
            });
            return Environment.ExitCode;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException) {
            if (ownsFixtureLog) { Log.Error(exception, "Native candidate fixture failed"); }
            Console.Error.WriteLine("WinUI candidate startup failure: " + exception);
            return 1;
        }
        finally { if (ownsFixtureLog) { Log.CloseAndFlush(); } }
    }
}

