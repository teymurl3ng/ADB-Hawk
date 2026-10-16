namespace WinUiShellProbe;

using System;
using System.Linq;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

internal static class Program {
    private static App? _application;
    private static ProbeWindow? _window;

    [STAThread]
    private static void Main() {
        ProbeDiagnostics.Start();
        AppDomain.CurrentDomain.UnhandledException += (sender, args) => {
            if (args.ExceptionObject is Exception exception) {
                ProbeDiagnostics.Fail(exception);
            }
        };
        WinRT.ComWrappersSupport.InitializeComWrappers();
        ProbeDiagnostics.Stage("ComWrappers initialized");
        Application.Start(parameters => {
            ProbeDiagnostics.Stage("Application startup callback");
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _application = new App();
            _application.UnhandledException += (sender, args) => {
                ProbeDiagnostics.Fail(args.Exception);
                args.Handled = true;
                Environment.Exit(1);
            };
            ProbeDiagnostics.Stage("Loading generated Application resources");
            _application.InitializeComponent();
            if (!DispatcherQueue.GetForCurrentThread().TryEnqueue(CreateWindow)) {
                throw new InvalidOperationException("The prototype window startup could not be dispatched.");
            }
        });
    }

    private static async void CreateWindow() {
        ProbeDiagnostics.Stage("Creating probe window after application startup");
        _window = new ProbeWindow();
        if (Environment.GetCommandLineArgs().Contains("--startup", StringComparer.Ordinal)) {
            string path = Environment.GetCommandLineArgs().Single(value => value.StartsWith("--verify=", StringComparison.Ordinal))["--verify=".Length..];
            await _window.VerifyStartupAsync(path).ConfigureAwait(true);
            return;
        }
        ProbeDiagnostics.Stage("Activating probe window");
        _window.Activate();
    }
}
