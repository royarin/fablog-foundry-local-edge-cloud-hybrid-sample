using System.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace FabLog.FabPad;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;
    public static DeviceHost Host { get; private set; } = null!;

    static readonly CancellationTokenSource Closing = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Host = new DeviceHost();

        var services = new ServiceCollection();
        services.AddWpfBlazorWebView();
        services.AddSingleton(Host);
        Services = services.BuildServiceProvider();

        // Fire and forget: the UI renders immediately and shows Host.Status while
        // the model loads. LoadAsync takes 8–23 s depending on the execution
        // provider — that cannot happen on the first note the technician writes.
        _ = Host.StartAsync(Closing.Token);
        _ = Host.PollAsync(Closing.Token);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Closing.Cancel();
        base.OnExit(e);
    }
}
