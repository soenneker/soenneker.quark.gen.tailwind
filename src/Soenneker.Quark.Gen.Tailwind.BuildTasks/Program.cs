using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Soenneker.Quark.Gen.Tailwind.BuildTasks;

public sealed class Program
{
    private static CancellationTokenSource? _cts;

    public static async Task Main(string[] args)
    {
        _cts = new CancellationTokenSource();
        Console.CancelKeyPress += OnCancelKeyPress;

        try
        {
            await CreateHostBuilder(args).RunConsoleAsync(_cts.Token);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Stopped program because of exception: {e}");
            throw;
        }
        finally
        {
            Console.CancelKeyPress -= OnCancelKeyPress;
            _cts.Dispose();
        }
    }

    public static IHostBuilder CreateHostBuilder(string[] args)
    {
        return Host.CreateDefaultBuilder(args)
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddConsole();
            })
            .ConfigureServices((_, services) =>
            {
                services.AddSingleton(new BuildTasksCommandLineArgs(args));
                Startup.ConfigureServices(services);
            });
    }

    private static void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs eventArgs)
    {
        eventArgs.Cancel = true;
        _cts?.Cancel();
    }
}
