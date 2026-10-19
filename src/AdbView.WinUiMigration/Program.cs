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
            if (!options.Live) { CandidateWindow.ValidateFixture(options.Fixture); }
            string identity = options.Live ? CandidateInstanceIdentity.Current() : options.InstanceName;
            using var instance = new Mutex(false, options.Live ? @"Local\" + identity : identity);
            bool created;
            try { created = instance.WaitOne(0); }
            catch (AbandonedMutexException) { created = true; Console.Error.WriteLine("Candidate acquired abandoned instance ownership."); }
            if (!created) {
                if (!options.Live) { throw new InvalidOperationException("Candidate fixture identity is already owned; no forwarding is permitted."); }
                if (options.StartupSilent) { return 0; }
                bool forwarded = CandidateControlChannel.SendToggleAsync(identity + ".Requests", TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
                if (!forwarded) { Console.Error.WriteLine("Candidate could not forward the user-open request."); }
                return forwarded ? 0 : 1;
            }
            try {
                if (options.Live || options.Fixture is "native-scenarios" or "native-bubble") {
                    string logPath = options.Live ? Path.Combine(AppContext.BaseDirectory, "candidate-live-redo.log")
                        : options.ResultPath is { } resultPath ? resultPath + ".native.log"
                        : Path.Combine(AppContext.BaseDirectory, "candidate-" + options.InstanceSuffix + ".native.log");
                    Log.Logger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.File(logPath,
                        formatProvider: System.Globalization.CultureInfo.InvariantCulture).CreateLogger();
                    ownsFixtureLog = true;
                    Log.Information("Native candidate fixture started with isolated instance {Instance}", options.InstanceName);
                }
                using var control = options.Live ? new CandidateControlChannel(identity + ".Requests") : null;
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
                            _window.StartLiveBackend();
                            control!.Start(() => _window.DispatcherQueue.TryEnqueue(_window.ToggleFromSecondaryLaunch), FatalControlFailure);
                            if (!options.StartupSilent) { _window.ShowSurface(); }
                            Log.Information("Isolated live candidate ready; startupSilent {StartupSilent}, visible {Visible}; environment integration incomplete", options.StartupSilent, _window.AppWindow.IsVisible);
                        }
                        else { _window.ShowSurface(); Stage("candidate activated"); }
                    })) {
                        throw new InvalidOperationException("Candidate startup dispatch was rejected.");
                    }
                });
                return Environment.ExitCode;
            }
            finally { instance.ReleaseMutex(); }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException) {
            if (ownsFixtureLog) { Log.Error(exception, "Native candidate fixture failed"); }
            Console.Error.WriteLine("WinUI candidate startup failure: " + exception);
            return 1;
        }
        finally { if (ownsFixtureLog) { Log.CloseAndFlush(); } }
    }
    private static void FatalControlFailure(Exception exception) {
        Log.Fatal(exception, "Candidate instance control failed"); Console.Error.WriteLine(exception); Environment.Exit(1);
    }
}

