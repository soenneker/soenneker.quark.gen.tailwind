using Microsoft.Extensions.DependencyInjection;
using Soenneker.Node.Util.Registrars;
using Soenneker.Quark.Gen.Tailwind.BuildTasks.Abstract;
using Soenneker.Utils.Directory.Registrars;
using Soenneker.Utils.File.Registrars;

namespace Soenneker.Quark.Gen.Tailwind.BuildTasks;

/// <summary>
/// Represents the startup.
/// </summary>
public static class Startup
{
    /// <summary>
    /// Configures services.
    /// </summary>
    /// <param name="services">The service collection.</param>
    public static void ConfigureServices(IServiceCollection services)
    {
        services.AddFileUtilAsSingleton().AddDirectoryUtilAsSingleton().AddNodeUtilAsSingleton().AddSingleton<ITailwindGeneratorRunner, TailwindGeneratorRunner>();
        services.AddHostedService<ConsoleHostedService>();
    }
}