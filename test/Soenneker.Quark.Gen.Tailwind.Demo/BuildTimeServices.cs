using Microsoft.Extensions.DependencyInjection;
using Soenneker.Quark;

namespace Soenneker.Quark.Gen.Tailwind.Demo;

/// <summary>
/// Registers services for the app. Invoked at runtime by Program.cs and at build time by
/// Soenneker.Quark.Gen.Tailwind BuildTasks for headless rendering. Decorate here.
/// </summary>
public static class BuildTimeServices
{
    [ConfigureBuildTimeServices]
    public static void Configure(IServiceCollection services) => Configure(services, "https://localhost/");

    public static void Configure(IServiceCollection services, string baseAddress)
    {
        services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(baseAddress) });
    }
}
