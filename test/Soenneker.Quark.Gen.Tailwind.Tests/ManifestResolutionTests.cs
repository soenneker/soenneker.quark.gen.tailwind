using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Soenneker.Quark.Gen.Tailwind.BuildTasks;

namespace Soenneker.Quark.Gen.Tailwind.Tests;

public sealed class ManifestResolutionTests
{
    [Test]
    [Arguments("$(LocalQuarkSuiteProject)", false)]
    [Arguments("Missing.csproj", false)]
    [Arguments("LocalSuite/Quark.csproj", true)]
    public async Task ProjectReferenceResolutionUsesExistingProjectsOrRestoredPackage(string include, bool localProjectExists, CancellationToken cancellationToken)
    {
        string root = Path.Combine(Path.GetTempPath(), "quark-manifest-" + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(root, "tailwind"));
        Directory.CreateDirectory(Path.Combine(root, "obj"));
        string packages = Path.Combine(root, "packages");
        string packageManifest = Path.Combine(packages, "soenneker.quark.suite", "4.0.1", "tailwind", "quark-suite-tailwind-manifest.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(packageManifest)!);
        var services = new ServiceCollection();
        services.AddLogging();
        Startup.ConfigureServices(services);
        await using ServiceProvider provider = services.BuildServiceProvider();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "App.csproj"), $$"""
                <Project><ItemGroup>
                  <ProjectReference Include="{{include}}" />
                </ItemGroup></Project>
                """, cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(root, "tailwind", "quark-suite-tailwind-manifest.txt"), "stale", cancellationToken);
            await File.WriteAllTextAsync(packageManifest, "md:w-[2.5rem]\nw-[calc(100%-2rem)]\nwhitespace-pre-line", cancellationToken);
            string expected = packageManifest;
            if (localProjectExists)
            {
                string localDirectory = Path.Combine(root, "LocalSuite");
                Directory.CreateDirectory(Path.Combine(localDirectory, "tailwind"));
                await File.WriteAllTextAsync(Path.Combine(localDirectory, "Quark.csproj"), "<Project />", cancellationToken);
                expected = Path.Combine(localDirectory, "tailwind", "quark-suite-tailwind-manifest.txt");
                await File.WriteAllTextAsync(expected, "local-suite", cancellationToken);
            }
            string assets = JsonSerializer.Serialize(new
            {
                libraries = new Dictionary<string, object> { ["Soenneker.Quark.Suite/4.0.1"] = new { } },
                packageFolders = new Dictionary<string, object> { [packages] = new { } }
            });
            await File.WriteAllTextAsync(Path.Combine(root, "obj", "project.assets.json"), assets, cancellationToken);
            var runner = ActivatorUtilities.CreateInstance<TailwindGeneratorRunner>(provider);
            MethodInfo resolve = typeof(TailwindGeneratorRunner).GetMethod("ResolveManifestPath", BindingFlags.NonPublic | BindingFlags.Instance)!;
            string? result = await (Task<string?>)resolve.Invoke(runner, [root, new Dictionary<string, string>(), cancellationToken])!;
            if (result != expected)
                throw new InvalidOperationException($"Expected {expected}, received {result}.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
