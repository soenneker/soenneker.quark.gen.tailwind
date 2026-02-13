using System;
using System.Reflection;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Soenneker.Blazor.StaticNavigationManagers.Registrars;
using Soenneker.Blazor.Utils.NoOpJSRuntime.Registrars;
using Soenneker.Blazor.Utils.ResourceLoader.Abstract;
using Soenneker.Quark;
using Soenneker.Quark.Build;

namespace Soenneker.Quark.Gen.Tailwind.BuildTasks;

/// <summary>
/// Builds a minimal service provider for headless Blazor component rendering.
/// Registers NavigationManager, IJSRuntime, QuarkOptions, INavigationInterception (no-op), and optionally
/// invokes [ConfigureBuildTimeServices] methods from the target assembly for app-specific registrations.
/// </summary>
internal static class BlazorRenderServiceProvider
{
    /// <summary>
    /// Creates a service provider with services required for rendering Blazor components.
    /// Discovers and invokes static methods marked with [ConfigureBuildTimeServices] in the target assembly.
    /// </summary>
    /// <param name="targetAssembly">The target app assembly to scan for [ConfigureBuildTimeServices] methods. If null, only base services are registered.</param>
    public static IServiceProvider Create(Assembly? targetAssembly)
    {
        var services = new ServiceCollection();

        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));

        services.AddStaticNavigationManagerAsScoped();
        services.AddNoOpJSRuntimeAsScoped();

        services.AddScoped<INavigationInterception, NoOpNavigationInterception>();
        services.AddScoped<IScrollToLocationHash, NoOpScrollToLocationHash>();

        services.AddSingleton(new QuarkOptions());
        // All Suite interops (Bar, Snackbar, Offcanvas, etc.) use IResourceLoader.LoadStyle/LoadScript which can hang at build time. One no-op for all.
        services.AddScoped<IResourceLoader, NoOpResourceLoader>();
        services.AddQuarkSuiteAsScoped();

        if (targetAssembly is not null)
            InvokeConfigureBuildTimeServices(targetAssembly, services);

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Creates a minimal service provider with only the bare minimum required for headless rendering.
    /// Use as a fallback for components that fail with the full provider (e.g. components that
    /// require app-specific services we cannot register at build time).
    /// Registers only logging, NavigationManager, IJSRuntime, and INavigationInterception (no-op).
    /// </summary>
    public static IServiceProvider CreateMinimal()
    {
        var services = new ServiceCollection();

        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));

        services.AddStaticNavigationManagerAsScoped();
        services.AddNoOpJSRuntimeAsScoped();

        services.AddScoped<INavigationInterception, NoOpNavigationInterception>();
        services.AddScoped<IScrollToLocationHash, NoOpScrollToLocationHash>();

        return services.BuildServiceProvider();
    }

    private static void InvokeConfigureBuildTimeServices(Assembly assembly, IServiceCollection services)
    {
        Type attributeType = typeof(ConfigureBuildTimeServicesAttribute);
        Type serviceCollectionType = typeof(IServiceCollection);

        try
        {
            foreach (Type type in assembly.GetExportedTypes())
            {
                foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (method.GetCustomAttribute(attributeType) is null)
                        continue;

                    ParameterInfo[] parameters = method.GetParameters();
                    if (parameters.Length != 1 || !serviceCollectionType.IsAssignableFrom(parameters[0].ParameterType))
                        continue;

                    if (method.ReturnType != typeof(void))
                        continue;

                    try
                    {
                        method.Invoke(null, [services]);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"ConfigureBuildTimeServices ({type.FullName}.{method.Name}) failed: {ex.InnerException?.Message ?? ex.Message}");
                    }
                }
            }
        }
        catch (ReflectionTypeLoadException ex)
        {
            Console.WriteLine($"Could not load types from assembly for ConfigureBuildTimeServices: {ex.Message}");
        }
    }
}
