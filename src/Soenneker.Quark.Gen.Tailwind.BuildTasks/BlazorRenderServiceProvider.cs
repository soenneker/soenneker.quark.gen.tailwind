using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Soenneker.Blazor.StaticNavigationManagers.Registrars;
using Soenneker.Blazor.Utils.NoOpJSRuntime.Registrars;

namespace Soenneker.Quark.Gen.Tailwind.BuildTasks;

/// <summary>
/// Builds a minimal service provider for headless Blazor component rendering.
/// Registers NavigationManager, IJSRuntime, and QuarkOptions for Quark apps.
/// </summary>
internal static class BlazorRenderServiceProvider
{
    /// <summary>
    /// Creates a service provider with services required for rendering Blazor components.
    /// </summary>
    public static IServiceProvider Create()
    {
        var services = new ServiceCollection();

        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));

        services.AddStaticNavigationManagerAsScoped();
        services.AddNoOpJSRuntimeAsScoped();

        services.AddSingleton(new QuarkOptions());

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Creates a minimal service provider with only the bare minimum required for headless rendering.
    /// Use as a fallback for components that fail with the full provider (e.g. components that
    /// require app-specific services we cannot register at build time).
    /// Registers only logging, NavigationManager, and IJSRuntime (no QuarkOptions or other app services).
    /// </summary>
    public static IServiceProvider CreateMinimal()
    {
        var services = new ServiceCollection();

        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));

        services.AddStaticNavigationManagerAsScoped();
        services.AddNoOpJSRuntimeAsScoped();

        return services.BuildServiceProvider();
    }
}
