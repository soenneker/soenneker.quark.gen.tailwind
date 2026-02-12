using Microsoft.Extensions.DependencyInjection;
using Soenneker.Node.Util.Registrars;
using Soenneker.Quark.Gen.Tailwind.BuildTasks.Abstract;
using Soenneker.Utils.CommandLineArgs.Registrars;

namespace Soenneker.Quark.Gen.Tailwind.BuildTasks;

public static class Startup
{
    public static void ConfigureServices(IServiceCollection services)
    {
        services.AddNodeUtilAsScoped();
        services.AddScoped<ITailwindGeneratorRunner, TailwindGeneratorRunner>();
        services.AddHostedService<ConsoleHostedService>();
        services.AddCommandLineArgsUtilAsScoped();
    }
}
