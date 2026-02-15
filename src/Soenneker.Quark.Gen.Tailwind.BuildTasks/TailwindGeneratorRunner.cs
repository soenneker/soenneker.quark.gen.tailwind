using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Soenneker.Blazor.Utils.ComponentHtmlRenderers;
using Soenneker.Extensions.String;
using Soenneker.Extensions.Task;
using Soenneker.Extensions.ValueTask;
using Soenneker.Node.Util.Abstract;
using Soenneker.Quark.Gen.Tailwind.BuildTasks.Abstract;
using Soenneker.Utils.CommandLineArgs.Abstract;
using Soenneker.Utils.Directory.Abstract;
using Soenneker.Utils.File.Abstract;
using Soenneker.Utils.Process.Abstract;

namespace Soenneker.Quark.Gen.Tailwind.BuildTasks;

/// <inheritdoc cref="ITailwindGeneratorRunner"/>
public sealed class TailwindGeneratorRunner : ITailwindGeneratorRunner
{
    private readonly INodeUtil _nodeUtil;
    private readonly IProcessUtil _processUtil;
    private readonly ICommandLineArgsUtil _commandLineArgsUtil;
    private readonly IDirectoryUtil _directoryUtil;
    private readonly IFileUtil _fileUtil;

    public TailwindGeneratorRunner(INodeUtil nodeUtil, IProcessUtil processUtil, ICommandLineArgsUtil commandLineArgsUtil, IDirectoryUtil directoryUtil, IFileUtil fileUtil)
    {
        _nodeUtil = nodeUtil;
        _processUtil = processUtil;
        _commandLineArgsUtil = commandLineArgsUtil;
        _directoryUtil = directoryUtil;
        _fileUtil = fileUtil;
    }

    private const string GeneratedContentFileName = "TailwindElements.txt";
    private const string TailwindDirName = "tailwind";
    /// <summary>Max time to wait for a single component render; prevents one hanging component from blocking the build.</summary>
    private static readonly TimeSpan PerComponentRenderTimeout = TimeSpan.FromSeconds(15);
    /// <summary>Output path for Tailwind CLI relative to the tailwind directory. Override with --tailwindOutput or MSBuild TailwindOutput.</summary>
    private const string DefaultTailwindOutputRelative = "../wwwroot/css/quark-tailwind.css";

    /// <summary>Regex to extract tag name and class value from elements: &lt;tag ... class="..." ...&gt;</summary>
    private static readonly Regex ElementWithClassRegex = new(
        @"<(\w+)[^>]*\bclass\s*=\s*[""']([^""']*)[""'][^>]*>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public async ValueTask<int> Run(CancellationToken cancellationToken = default)
    {
        if (!_commandLineArgsUtil.TryGet("--targetPath", out string? targetPath) || targetPath.IsNullOrWhiteSpace())
            return Fail("Missing required --targetPath");
        if (!_commandLineArgsUtil.TryGet("--projectDir", out string? projectDir) || projectDir.IsNullOrWhiteSpace())
            return Fail("Missing required --projectDir");

        targetPath = Path.GetFullPath(targetPath.Trim().Trim('"'));
        projectDir = Path.GetFullPath(projectDir.Trim().Trim('"'));

        if (!await _fileUtil.Exists(targetPath, cancellationToken).NoSync())
            return Fail($"Target assembly not found: {targetPath}");

        string targetDir = Path.GetDirectoryName(targetPath)!;
        Assembly? ResolveFromTargetDir(object? sender, ResolveEventArgs args)
        {
            try
            {
                string simpleName = new AssemblyName(args.Name).Name ?? string.Empty;
                string candidate = Path.Combine(targetDir, simpleName + ".dll");
                return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
            }
            catch
            {
                return null;
            }
        }
        Assembly? ResolveFromTargetDirAlc(AssemblyLoadContext context, AssemblyName name)
        {
            try
            {
                string simpleName = name.Name ?? string.Empty;
                string candidate = Path.Combine(targetDir, simpleName + ".dll");
                return File.Exists(candidate) ? context.LoadFromAssemblyPath(candidate) : null;
            }
            catch
            {
                return null;
            }
        }
        try
        {
            AppDomain.CurrentDomain.AssemblyResolve += ResolveFromTargetDir;
            AssemblyLoadContext.Default.Resolving += ResolveFromTargetDirAlc;
            Assembly appAssembly;
            try
            {
                appAssembly = Assembly.LoadFrom(targetPath);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to load assembly: {ex.Message}");
                return 1;
            }

            HashSet<string> uniqueElements = await CollectUniqueElementsAsync(appAssembly, projectDir, _fileUtil, cancellationToken).NoSync();

            string tailwindDir = Path.Combine(projectDir, TailwindDirName);

            await _directoryUtil.CreateIfDoesNotExist(tailwindDir, true, cancellationToken)
                          .NoSync();

            await EnsureTailwindFilesExist(tailwindDir, cancellationToken).NoSync();

            string generatedPath = Path.Combine(tailwindDir, GeneratedContentFileName);

            // Always write TailwindElements.txt so the file exists (Tailwind content scan); one line per unique element.
            var lines = new List<string> { "/* Generated by Soenneker.Quark.Gen.Tailwind BuildTasks - do not edit */" };
            foreach (string element in uniqueElements.OrderBy(e => e, StringComparer.Ordinal))
                lines.Add(element);
            string content = string.Join(Environment.NewLine, lines);
            await _fileUtil.Write(generatedPath, content, log: false, cancellationToken).NoSync();

            if (uniqueElements.Count == 0)
                return 0; // No elements to scan; skip Tailwind CLI, file was still written

            string configPath = Path.Combine(tailwindDir, "tailwind.config.js");
            if (await _fileUtil.Exists(configPath, cancellationToken))
                await EnsureGeneratedContentInConfig(configPath, GeneratedContentFileName, cancellationToken).NoSync();

            string inputCss = Path.Combine(tailwindDir, "input.css");
            string outputCss = _commandLineArgsUtil.TryGet("--tailwindOutput", out string? outPath) && !string.IsNullOrWhiteSpace(outPath)
                ? outPath.Trim().Trim('"')
                : DefaultTailwindOutputRelative;

            if (!await _fileUtil.Exists(inputCss, cancellationToken))
                return Fail($"Tailwind input not found: {inputCss}");

            await _nodeUtil.NpmInstall(tailwindDir, cancellationToken: cancellationToken)
                           .NoSync();

            int exitCode = await RunTailwindCli(tailwindDir, configPath, inputCss, outputCss, cancellationToken).NoSync();
            if (exitCode != 0)
            {
                Console.Error.WriteLine($"Tailwind CLI exited with code {exitCode}. {GeneratedContentFileName} was written; ensure Node/npx and @tailwindcss/cli are available to compile CSS.");
                return 0; // Do not fail the build; the generated file was written successfully.
            }
            return 0;
        }
        finally
        {
            AppDomain.CurrentDomain.AssemblyResolve -= ResolveFromTargetDir;
            AssemblyLoadContext.Default.Resolving -= ResolveFromTargetDirAlc;
        }
    }

    /// <summary>Collects unique elements from Quark component usages (parse + render) and optionally from full component discovery as fallback.</summary>
    private async Task<HashSet<string>> CollectUniqueElementsAsync(Assembly assembly, string projectDir, IFileUtil fileUtil, CancellationToken cancellationToken)
    {
        var uniqueElements = new HashSet<string>(StringComparer.Ordinal);
        IServiceProvider serviceProvider = BlazorRenderServiceProvider.Create(assembly);

        await using (var renderer = new ComponentHtmlRenderer(serviceProvider, disposeServiceProvider: false))
        {
            Dictionary<string, Type> tagToType = QuarkComponentUsageCollector.BuildTagToTypeMap(assembly);
            HashSet<string> fromQuark = await QuarkComponentUsageCollector.Collect(assembly, projectDir, renderer, tagToType, fileUtil, cancellationToken).NoSync();
            foreach (string e in fromQuark)
                uniqueElements.Add(e);
        }

        if (uniqueElements.Count == 0)
        {
            HashSet<string> fromFull = await CollectUniqueElementsFromRenderedComponents(assembly, cancellationToken).NoSync();
            foreach (string e in fromFull)
                uniqueElements.Add(e);
        }

        return uniqueElements;
    }

    /// <summary>Runs a render task with a timeout so one hanging component does not block the build.</summary>
    private static async Task<string?> RenderWithTimeout(
        Func<Task<string?>> render,
        TimeSpan timeout,
        string nameForLog,
        CancellationToken cancellationToken)
    {
        Task<string?> renderTask = render();
        Task delayTask = Task.Delay(timeout, cancellationToken);

        Task completed = await Task.WhenAny(renderTask, delayTask).NoSync();
        if (completed == renderTask)
            return await renderTask.NoSync();
        Console.WriteLine($"Skipped {nameForLog}: render timed out after {(int)timeout.TotalSeconds}s.");
        return null;
    }

    /// <summary>Discovers all Blazor components in the assembly, renders each to HTML via ComponentHtmlRenderer, and extracts unique elements (tag + class) per line.</summary>
    private static async Task<HashSet<string>> CollectUniqueElementsFromRenderedComponents(Assembly assembly, CancellationToken cancellationToken)
    {
        List<Type> componentTypes = DiscoverComponentTypes(assembly);

        if (componentTypes.Count == 0)
            return [];

        IServiceProvider serviceProvider = BlazorRenderServiceProvider.Create(assembly);
        var uniqueElements = new HashSet<string>(StringComparer.Ordinal);

        await using (var renderer = new ComponentHtmlRenderer(serviceProvider, disposeServiceProvider: false))
        {
            foreach (Type componentType in componentTypes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string? html = null;
                try
                {
                    html = await RenderWithTimeout(
                        () => renderer.RenderToHtml(componentType, htmlDecode: true),
                        PerComponentRenderTimeout,
                        componentType.FullName ?? componentType.Name,
                        cancellationToken).NoSync();
                }
                catch (Exception ex)
                {
                    // Fallback: try with minimal service provider for components we can't register (e.g. app-specific DI).
                    try
                    {
                        IServiceProvider minimalProvider = BlazorRenderServiceProvider.CreateMinimal();

                        await using (var minimalRenderer = new ComponentHtmlRenderer(minimalProvider, disposeServiceProvider: true))
                        {
                            html = await RenderWithTimeout(
                                () => minimalRenderer.RenderToHtml(componentType, htmlDecode: true),
                                PerComponentRenderTimeout,
                                componentType.FullName ?? componentType.Name,
                                cancellationToken).NoSync();
                        }
                    }
                    catch (Exception)
                    {
                        // Skip components that fail with both full and minimal provider.
                        Console.WriteLine($"Skipped {componentType.FullName}: {ex.Message}");
                    }
                }

                if (!string.IsNullOrEmpty(html))
                {
                    foreach (string element in ExtractUniqueElementsFromHtml(html))
                        uniqueElements.Add(element);
                }
            }
        }

        return uniqueElements;
    }

    /// <summary>Finds all types in the assembly that implement IComponent (Blazor components).</summary>
    private static List<Type> DiscoverComponentTypes(Assembly assembly)
    {
        Type componentBaseType = typeof(ComponentBase);
        Type iComponentType = typeof(IComponent);
        var list = new List<Type>();

        try
        {
            foreach (Type type in assembly.GetExportedTypes())
            {
                if (type.IsAbstract || type.IsInterface)
                    continue;
                if (!componentBaseType.IsAssignableFrom(type) && !iComponentType.IsAssignableFrom(type))
                    continue;
                if (type.Assembly != assembly)
                    continue; // Only components defined in the app assembly, not dependencies
                list.Add(type);
            }
        }
        catch (ReflectionTypeLoadException ex)
        {
            Console.Error.WriteLine($"Could not load some types from assembly: {ex.Message}");
        }

        return list;
    }

    /// <summary>Extracts unique elements from HTML (tag + normalized class). Each yielded string is one full element line, as HTML given to the browser (renderer already uses minimal attribute encoding).</summary>
    private static IEnumerable<string> ExtractUniqueElementsFromHtml(string html)
    {
        foreach (Match m in ElementWithClassRegex.Matches(html))
        {
            string tag = m.Groups[1].Value;
            string classValue = m.Groups[2].Value;
            if (string.IsNullOrWhiteSpace(classValue))
                continue;
            // Normalize: trim, collapse whitespace, sort so "col-md-10 col" and "col col-md-10" are the same element.
            string normalizedClasses = string.Join(" ", classValue
                .Split((char[]?)[' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .OrderBy(s => s, StringComparer.Ordinal));
            if (normalizedClasses.Length == 0)
                continue;
            yield return $"<{tag} class=\"{normalizedClasses}\"></{tag}>";
        }
    }

    /// <summary>Creates input.css, tailwind.config.js, and package.json in the tailwind directory if they do not exist.</summary>
    private async ValueTask EnsureTailwindFilesExist(string tailwindDir, CancellationToken cancellationToken)
    {
        string inputCssPath = Path.Combine(tailwindDir, "input.css");
        if (!await _fileUtil.Exists(inputCssPath, cancellationToken))
        {
            const string css = """
                               @import "tailwindcss";
                               @import "tw-animate-css";
                               
                               @custom-variant dark (&:is(.dark *));
                               
                               :root {
                                 --background: oklch(1 0 0);
                                 --foreground: oklch(0.145 0 0);
                                 --card: oklch(1 0 0);
                                 --card-foreground: oklch(0.145 0 0);
                                 --popover: oklch(1 0 0);
                                 --popover-foreground: oklch(0.145 0 0);
                                 --primary: oklch(0.205 0 0);
                                 --primary-foreground: oklch(0.985 0 0);
                                 --secondary: oklch(0.97 0 0);
                                 --secondary-foreground: oklch(0.205 0 0);
                                 --muted: oklch(0.97 0 0);
                                 --muted-foreground: oklch(0.556 0 0);
                                 --accent: oklch(0.97 0 0);
                                 --accent-foreground: oklch(0.205 0 0);
                                 --destructive: oklch(0.577 0.245 27.325);
                                 --destructive-foreground: oklch(0.577 0.245 27.325);
                                 --border: oklch(0.922 0 0);
                                 --input: oklch(0.922 0 0);
                                 --ring: oklch(0.708 0 0);
                                 --chart-1: oklch(0.646 0.222 41.116);
                                 --chart-2: oklch(0.6 0.118 184.704);
                                 --chart-3: oklch(0.398 0.07 227.392);
                                 --chart-4: oklch(0.828 0.189 84.429);
                                 --chart-5: oklch(0.769 0.188 70.08);
                                 --radius: 0.625rem;
                                 --sidebar: oklch(0.985 0 0);
                                 --sidebar-foreground: oklch(0.145 0 0);
                                 --sidebar-primary: oklch(0.205 0 0);
                                 --sidebar-primary-foreground: oklch(0.985 0 0);
                                 --sidebar-accent: oklch(0.97 0 0);
                                 --sidebar-accent-foreground: oklch(0.205 0 0);
                                 --sidebar-border: oklch(0.922 0 0);
                                 --sidebar-ring: oklch(0.708 0 0);
                               }
                               
                               .dark {
                                 --background: oklch(0.145 0 0);
                                 --foreground: oklch(0.985 0 0);
                                 --card: oklch(0.145 0 0);
                                 --card-foreground: oklch(0.985 0 0);
                                 --popover: oklch(0.145 0 0);
                                 --popover-foreground: oklch(0.985 0 0);
                                 --primary: oklch(0.985 0 0);
                                 --primary-foreground: oklch(0.205 0 0);
                                 --secondary: oklch(0.269 0 0);
                                 --secondary-foreground: oklch(0.985 0 0);
                                 --muted: oklch(0.269 0 0);
                                 --muted-foreground: oklch(0.708 0 0);
                                 --accent: oklch(0.269 0 0);
                                 --accent-foreground: oklch(0.985 0 0);
                                 --destructive: oklch(0.396 0.141 25.723);
                                 --destructive-foreground: oklch(0.637 0.237 25.331);
                                 --border: oklch(0.269 0 0);
                                 --input: oklch(0.269 0 0);
                                 --ring: oklch(0.439 0 0);
                                 --chart-1: oklch(0.488 0.243 264.376);
                                 --chart-2: oklch(0.696 0.17 162.48);
                                 --chart-3: oklch(0.769 0.188 70.08);
                                 --chart-4: oklch(0.627 0.265 303.9);
                                 --chart-5: oklch(0.645 0.246 16.439);
                                 --sidebar: oklch(0.205 0 0);
                                 --sidebar-foreground: oklch(0.985 0 0);
                                 --sidebar-primary: oklch(0.488 0.243 264.376);
                                 --sidebar-primary-foreground: oklch(0.985 0 0);
                                 --sidebar-accent: oklch(0.269 0 0);
                                 --sidebar-accent-foreground: oklch(0.985 0 0);
                                 --sidebar-border: oklch(0.269 0 0);
                                 --sidebar-ring: oklch(0.439 0 0);
                               }
                               
                               @theme inline {
                                 --color-background: var(--background);
                                 --color-foreground: var(--foreground);
                                 --color-card: var(--card);
                                 --color-card-foreground: var(--card-foreground);
                                 --color-popover: var(--popover);
                                 --color-popover-foreground: var(--popover-foreground);
                                 --color-primary: var(--primary);
                                 --color-primary-foreground: var(--primary-foreground);
                                 --color-secondary: var(--secondary);
                                 --color-secondary-foreground: var(--secondary-foreground);
                                 --color-muted: var(--muted);
                                 --color-muted-foreground: var(--muted-foreground);
                                 --color-accent: var(--accent);
                                 --color-accent-foreground: var(--accent-foreground);
                                 --color-destructive: var(--destructive);
                                 --color-destructive-foreground: var(--destructive-foreground);
                                 --color-border: var(--border);
                                 --color-input: var(--input);
                                 --color-ring: var(--ring);
                                 --color-chart-1: var(--chart-1);
                                 --color-chart-2: var(--chart-2);
                                 --color-chart-3: var(--chart-3);
                                 --color-chart-4: var(--chart-4);
                                 --color-chart-5: var(--chart-5);
                                 --radius-sm: calc(var(--radius) - 4px);
                                 --radius-md: calc(var(--radius) - 2px);
                                 --radius-lg: var(--radius);
                                 --radius-xl: calc(var(--radius) + 4px);
                                 --color-sidebar: var(--sidebar);
                                 --color-sidebar-foreground: var(--sidebar-foreground);
                                 --color-sidebar-primary: var(--sidebar-primary);
                                 --color-sidebar-primary-foreground: var(--sidebar-primary-foreground);
                                 --color-sidebar-accent: var(--sidebar-accent);
                                 --color-sidebar-accent-foreground: var(--sidebar-accent-foreground);
                                 --color-sidebar-border: var(--sidebar-border);
                                 --color-sidebar-ring: var(--sidebar-ring);
                               }
                               
                               @layer base {
                                 * {
                                   @apply border-border outline-ring/50;
                                 }
                                 body {
                                   @apply bg-background text-foreground;
                                 }
                               }
                               """;

            await _fileUtil.Write(inputCssPath, css, log: false, cancellationToken).NoSync();
        }

        string configPath = Path.Combine(tailwindDir, "tailwind.config.js");
        if (!await _fileUtil.Exists(configPath, cancellationToken))
        {
            const string configContent = @"export default {
  content: [
    ""./TailwindElements.txt"",
    ""./**/*.txt"",
    ""../**/*.razor"",
    ""../**/*.cshtml"",
    ""../**/*.html""
  ],
  plugins: [
  require(""tw-animate-css"")
]
};
";
            await _fileUtil.Write(configPath, configContent, log: false, cancellationToken).NoSync();
        }

        string packageJsonPath = Path.Combine(tailwindDir, "package.json");
        const string packageJsonContent = "{\"name\":\"tailwind\",\"private\":true,\"devDependencies\":{\"@tailwindcss/cli\":\"^4.0.0\",\"tailwindcss\":\"^4.0.0\", \"tw-animate-css\": \"^1.4.0\"},\"scripts\":{\"build\":\"npx @tailwindcss/cli -c ./tailwind.config.js -i ./input.css -o ../wwwroot/css/quark-tailwind.css\",\"watch\":\"npx @tailwindcss/cli -c ./tailwind.config.js -i ./input.css -o ../wwwroot/css/quark-tailwind.css --watch\"}}\n";
        if (!await _fileUtil.Exists(packageJsonPath, cancellationToken))
        {
            await _fileUtil.Write(packageJsonPath, packageJsonContent, log: false, cancellationToken).NoSync();
        }
        else
        {
            byte[] existing = await _fileUtil.ReadToBytes(packageJsonPath, log: false, cancellationToken).NoSync();
            if (existing.Length >= 3 && existing[0] == 0xEF && existing[1] == 0xBB && existing[2] == 0xBF)
                await _fileUtil.Write(packageJsonPath, packageJsonContent, log: false, cancellationToken).NoSync();
        }
    }

    private async ValueTask EnsureGeneratedContentInConfig(string configPath, string generatedFileName, CancellationToken cancellationToken)
    {
        string text = await _fileUtil.Read(configPath, log: false, cancellationToken).NoSync();
        if (text.Contains(generatedFileName, StringComparison.Ordinal))
            return;

        var marker = "content:";
        int idx = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return;
        int afterContent = idx + marker.Length;
        int bracket = text.IndexOf('[', afterContent);
        if (bracket < 0)
            return;
        int insertAt = bracket + 1;
        var entry = $"./{generatedFileName}";
        var toInsert = $" \"{entry}\",";
        text = text.Insert(insertAt, toInsert);
        await _fileUtil.Write(configPath, text, log: false, cancellationToken).NoSync();
    }

    private async Task<int> RunTailwindCli(string workingDir, string configPath, string inputCss, string outputCss, CancellationToken cancellationToken)
    {
        string inputFileName = Path.GetFileName(inputCss);
        bool hasConfig = await _fileUtil.Exists(configPath, cancellationToken);
        string? configFileName = hasConfig ? Path.GetFileName(configPath) : null;

        var argList = new List<string> { "@tailwindcss/cli" };
        if (hasConfig && configFileName.HasContent())
        {
            argList.Add("-c");
            argList.Add(configFileName);
        }
        argList.Add("-i");
        argList.Add(inputFileName);
        argList.Add("-o");
        argList.Add(outputCss);

        string arguments = string.Join(" ", argList.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
        string npxPath = await _nodeUtil.GetNpxPath(cancellationToken).NoSync();

        try
        {
            // Cap wait at 3 minutes so the build cannot hang indefinitely (e.g. if npx or Tailwind CLI stalls).
            await _processUtil.Start(
                npxPath,
                workingDir,
                arguments,
                admin: false,
                waitForExit: true,
                timeout: TimeSpan.FromSeconds(15),
                log: true,
                environmentalVars: null,
                cancellationToken).NoSync();
            return 0;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("exited with code"))
        {
            Console.Error.WriteLine($"{GeneratedContentFileName} was written; ensure Node/npx and @tailwindcss/cli are available to compile CSS.");
            return 0;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Failed to start"))
        {
            Console.Error.WriteLine($"Failed to start Tailwind CLI: {ex.Message}. Tried npx at: {npxPath}. Ensure Node is installed.");
            return 1;
        }
        catch (OperationCanceledException)
        {
            return 1;
        }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }
}
