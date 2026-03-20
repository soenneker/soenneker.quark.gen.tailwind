using Microsoft.Extensions.DependencyInjection;
using Soenneker.Node.Util.Registrars;
using Soenneker.Quark.Gen.Tailwind.BuildTasks.Abstract;
using Soenneker.Utils.Directory.Registrars;
using Soenneker.Utils.File.Registrars;

namespace Soenneker.Quark.Gen.Tailwind.BuildTasks;

public static class Startup
{
    public static void ConfigureServices(IServiceCollection services)
    {
        services.AddFileUtilAsScoped()
                .AddDirectoryUtilAsScoped()
                .AddNodeUtilAsScoped();
        services.AddScoped<ITailwindGeneratorRunner, TailwindGeneratorRunner>();
        services.AddHostedService<ConsoleHostedService>();
    }
}