using Microsoft.Extensions.DependencyInjection;
using Soenneker.Quark.Gen.Tailwind.BuildTasks.Abstract;

namespace Soenneker.Quark.Gen.Tailwind.BuildTasks;

public static class Startup
{
    public static void ConfigureServices(IServiceCollection services)
    {
        services.AddScoped<ITailwindGeneratorRunner, TailwindGeneratorRunner>();
        services.AddHostedService<ConsoleHostedService>();
    }
}
