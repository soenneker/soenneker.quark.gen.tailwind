using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Soenneker.Node.Util.Abstract;
using Soenneker.Quark.Gen.Tailwind.BuildTasks.Abstract;
using Soenneker.Utils.Directory.Abstract;
using Soenneker.Utils.File.Abstract;
using Soenneker.Extensions.String;

namespace Soenneker.Quark.Gen.Tailwind.BuildTasks;

/// <inheritdoc cref="ITailwindGeneratorRunner"/>
public sealed class TailwindGeneratorRunner : ITailwindGeneratorRunner
{
    private const string _tailwindDirName = "tailwind";
    private static readonly string _intermediateTailwindDir = Path.Combine("obj", "quark", "tailwind");
    private const string _inlineGeneratedTxtFileName = "tw-inline.generated.txt";
    private const string _projectManifestFileName = "quark-tailwind-manifest.txt";
    private const string _suitePackageId = "soenneker.quark.suite";

    private readonly ILogger<TailwindGeneratorRunner> _logger;
    private readonly INodeUtil _nodeUtil;
    private readonly IFileUtil _fileUtil;
    private readonly IDirectoryUtil _directoryUtil;

    public TailwindGeneratorRunner(ILogger<TailwindGeneratorRunner> logger, INodeUtil nodeUtil, IFileUtil fileUtil, IDirectoryUtil directoryUtil)
    {
        _logger = logger;
        _nodeUtil = nodeUtil;
        _fileUtil = fileUtil;
        _directoryUtil = directoryUtil;
    }

    public async ValueTask<int> Run(CancellationToken cancellationToken = default)
    {
        string[] args = Environment.GetCommandLineArgs();
        Dictionary<string, string> map = ParseArgs(args);

        if (!map.TryGetValue("--projectDir", out string? projectDir) || string.IsNullOrWhiteSpace(projectDir))
            return Fail("Missing required --projectDir");

        projectDir = Path.GetFullPath(projectDir.Trim()
                                                .Trim('"'));

        _logger.LogInformation("Starting Tailwind generation for project {ProjectDir}.", projectDir);

        string tailwindDir = Path.Combine(projectDir, _intermediateTailwindDir);
        _logger.LogInformation("Preparing Tailwind working directory at {TailwindDir}.", tailwindDir);
        await _directoryUtil.Create(tailwindDir, log: false, cancellationToken);

        _logger.LogInformation("Resolving Tailwind manifest path...");
        string? manifestPath = await ResolveManifestPath(projectDir, map, cancellationToken);
        string? projectManifestPath = await ResolveProjectManifestPath(projectDir, cancellationToken);

        if (string.IsNullOrWhiteSpace(manifestPath) && string.IsNullOrWhiteSpace(projectManifestPath))
        {
            return Fail(
                $"Unable to locate '{_inlineGeneratedTxtFileName}' from a Soenneker.Quark.Suite project/package. " +
                "Reference Soenneker.Quark.Suite via ProjectReference or NuGet, or set --manifestPath explicitly.");
        }

        if (!string.IsNullOrWhiteSpace(manifestPath))
            _logger.LogInformation("Using upstream Tailwind manifest at {ManifestPath}.", manifestPath);

        if (!string.IsNullOrWhiteSpace(projectManifestPath))
            _logger.LogInformation("Using project Tailwind manifest at {ManifestPath}.", projectManifestPath);

        _logger.LogInformation("Staging Tailwind manifest(s) to {TailwindDir}.", tailwindDir);
        await StageManifestsToTailwindDir(manifestPath, projectManifestPath, tailwindDir, cancellationToken);

        string projectRootForCss = GetRelativePath(tailwindDir, projectDir);
        _logger.LogInformation("Ensuring Tailwind input.css, config, and package metadata exist.");
        await EnsureInputCss(tailwindDir, projectRootForCss, cancellationToken);
        await EnsureTailwindConfig(tailwindDir, cancellationToken);
        await EnsurePackageJson(tailwindDir, cancellationToken);

        try
        {
            _logger.LogInformation("Running npm install in {TailwindDir}.", tailwindDir);
            await _nodeUtil.NpmInstall(tailwindDir, cleanInstall: false, skipIfUpToDate: true, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "npm install failed. Continuing with Tailwind CLI.");
        }

        // Resolve output path: prefer --tailwindOutput, else projectDir/wwwroot/css/quark-tailwind.css (no ambiguity).
        string outputCssFull;
        if (map.TryGetValue("--tailwindOutput", out string? outPath) && !string.IsNullOrWhiteSpace(outPath))
        {
            outputCssFull = Path.GetFullPath(outPath.Trim()
                                                    .Trim('"'));
        }
        else
        {
            outputCssFull = Path.GetFullPath(Path.Combine(projectDir, "wwwroot", "css", "quark-tailwind.css"));
        }

        string? outputDir = Path.GetDirectoryName(outputCssFull);
        if (!string.IsNullOrEmpty(outputDir))
        {
            _logger.LogInformation("Ensuring Tailwind output directory exists at {OutputDir}.", outputDir);
            await _directoryUtil.Create(outputDir, log: false, cancellationToken);
        }

        // Pass path relative to tailwind dir so CLI writes to the correct file (avoids Windows absolute-path issues).
        string outputCssForCli = GetRelativePath(tailwindDir, outputCssFull);

        string inputCss = Path.Combine(tailwindDir, "input.css");
        string configPath = Path.Combine(tailwindDir, "tailwind.config.js");

        _logger.LogInformation("Running Tailwind CLI for full CSS output at {OutputCss}.", outputCssFull);
        int exitCode = await RunTailwindCli(tailwindDir, configPath, inputCss, outputCssForCli, minify: false, cancellationToken);
        if (exitCode != 0)
        {
            _logger.LogWarning("Tailwind CLI exited with code {ExitCode}. Ensure Node/npx and @tailwindcss/cli are available.", exitCode);
            return exitCode;
        }

        // Build minified version alongside (quark-tailwind.min.css in same directory).
        string minOutputCssFull = Path.Combine(Path.GetDirectoryName(outputCssFull)!, "quark-tailwind.min.css");
        string? outputDirForMin = Path.GetDirectoryName(minOutputCssFull);
        if (!string.IsNullOrEmpty(outputDirForMin))
        {
            _logger.LogInformation("Ensuring Tailwind minified output directory exists at {OutputDir}.", outputDirForMin);
            await _directoryUtil.Create(outputDirForMin, log: false, cancellationToken);
        }

        string outputCssForMin = GetRelativePath(tailwindDir, minOutputCssFull);
        _logger.LogInformation("Running Tailwind CLI for minified CSS output at {MinOutputCss}.", minOutputCssFull);
        exitCode = await RunTailwindCli(tailwindDir, configPath, inputCss, outputCssForMin, minify: true, cancellationToken);
        if (exitCode != 0)
        {
            _logger.LogWarning("Tailwind CLI (minify) exited with code {ExitCode}. Full CSS was built; minified output may be missing.", exitCode);
        }

        _logger.LogInformation("Completed Tailwind generation for project {ProjectDir}.", projectDir);
        return 0;
    }

    private static string GetRelativePath(string fromDir, string toPath)
    {
        string rel = Path.GetRelativePath(fromDir, toPath);
        return rel.Replace('\\', '/');
    }

    private async Task<string?> ResolveManifestPath(string projectDir, IReadOnlyDictionary<string, string> args, CancellationToken cancellationToken)
    {
        if (args.TryGetValue("--manifestPath", out string? explicitManifestPath) && !string.IsNullOrWhiteSpace(explicitManifestPath))
        {
            string fullPath = Path.GetFullPath(explicitManifestPath.Trim().Trim('"'));
            return await _fileUtil.Exists(fullPath, cancellationToken) ? fullPath : null;
        }

        string? projectReferenceManifest = await TryResolveManifestFromProjectReferences(projectDir, cancellationToken);
        if (!string.IsNullOrWhiteSpace(projectReferenceManifest))
            return projectReferenceManifest;

        string? packageManifest = await TryResolveManifestFromPackages(projectDir, cancellationToken);
        if (!string.IsNullOrWhiteSpace(packageManifest))
            return packageManifest;

        return null;
    }

    private async Task<string?> TryResolveManifestFromProjectReferences(string projectDir, CancellationToken cancellationToken)
    {
        string? projectFilePath = GetProjectFilePath(projectDir);
        if (string.IsNullOrWhiteSpace(projectFilePath))
            return null;

        XDocument document;
        try
        {
            string xml = await _fileUtil.Read(projectFilePath, log: false, cancellationToken);
            document = XDocument.Parse(xml, LoadOptions.None);
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Failed to inspect project references for {ProjectDir}", projectDir);
            return null;
        }

        IEnumerable<string?> includes = document.Descendants()
                                                .Where(element => element.Name.LocalName == "ProjectReference")
                                                .Select(element => element.Attribute("Include")?.Value);

        foreach (string? include in includes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(include))
                continue;

            string referencePath = Path.GetFullPath(Path.Combine(projectDir, include));
            string? referenceDirectory = Path.GetDirectoryName(referencePath);
            if (referenceDirectory.IsNullOrWhiteSpace())
                continue;

            string manifestPath = Path.Combine(referenceDirectory, _tailwindDirName, _inlineGeneratedTxtFileName);
            if (await _fileUtil.Exists(manifestPath, cancellationToken))
                return manifestPath;
        }

        return null;
    }

    private async Task<string?> TryResolveManifestFromPackages(string projectDir, CancellationToken cancellationToken)
    {
        string assetsPath = Path.Combine(projectDir, "obj", "project.assets.json");
        if (!await _fileUtil.Exists(assetsPath, cancellationToken))
            return null;

        try
        {
            string assetsJson = await _fileUtil.Read(assetsPath, log: false, cancellationToken);
            using JsonDocument document = JsonDocument.Parse(assetsJson);

            if (!document.RootElement.TryGetProperty("libraries", out JsonElement libraries) ||
                !document.RootElement.TryGetProperty("packageFolders", out JsonElement packageFolders))
            {
                return null;
            }

            string[] suiteLibraries = libraries.EnumerateObject()
                                             .Select(property => property.Name)
                                             .Where(name => name.StartsWith(_suitePackageId + "/", StringComparison.OrdinalIgnoreCase))
                                             .ToArray();

            if (suiteLibraries.Length == 0)
                return null;

            string[] folders = packageFolders.EnumerateObject()
                                             .Select(property => property.Name.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                                             .ToArray();

            foreach (string library in suiteLibraries)
            {
                int separatorIndex = library.IndexOf('/');
                if (separatorIndex < 0 || separatorIndex == library.Length - 1)
                    continue;

                string version = library.Substring(separatorIndex + 1);

                foreach (string folder in folders)
                {
                    string contentFilesManifestPath = Path.Combine(folder, _suitePackageId, version, "contentFiles", "any", "any", "tailwind",
                        _inlineGeneratedTxtFileName);

                    if (await _fileUtil.Exists(contentFilesManifestPath, cancellationToken))
                        return contentFilesManifestPath;

                    string legacyManifestPath = Path.Combine(folder, _suitePackageId, version, _tailwindDirName, _inlineGeneratedTxtFileName);
                    if (await _fileUtil.Exists(legacyManifestPath, cancellationToken))
                        return legacyManifestPath;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Failed to inspect package assets for {ProjectDir}", projectDir);
        }

        return null;
    }

    private async Task<string?> ResolveProjectManifestPath(string projectDir, CancellationToken cancellationToken)
    {
        string manifestPath = Path.Combine(projectDir, _tailwindDirName, _projectManifestFileName);
        return await _fileUtil.Exists(manifestPath, cancellationToken) ? manifestPath : null;
    }

    private static string? GetProjectFilePath(string projectDir)
    {
        string[] projectFiles = Directory.GetFiles(projectDir, "*.csproj", SearchOption.TopDirectoryOnly);
        if (projectFiles.Length == 0)
            return null;

        if (projectFiles.Length == 1)
            return projectFiles[0];

        string directoryName = Path.GetFileName(projectDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return projectFiles.FirstOrDefault(path => string.Equals(Path.GetFileNameWithoutExtension(path), directoryName, StringComparison.OrdinalIgnoreCase))
               ?? projectFiles[0];
    }

    private async Task StageManifestsToTailwindDir(string? upstreamManifestPath, string? projectManifestPath, string tailwindDir,
        CancellationToken cancellationToken)
    {
        string destinationPath = Path.Combine(tailwindDir, _inlineGeneratedTxtFileName);
        var manifestPaths = new List<string>(2);

        AddManifestPath(manifestPaths, upstreamManifestPath);
        AddManifestPath(manifestPaths, projectManifestPath);

        if (manifestPaths.Count == 0)
            return;

        var lines = new SortedSet<string>(StringComparer.Ordinal);

        foreach (string manifestPath in manifestPaths)
        {
            string contents = await _fileUtil.Read(manifestPath, log: false, cancellationToken);

            foreach (string rawLine in contents.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                string line = rawLine.Trim();

                if (line.Length == 0 || line[0] == '#')
                    continue;

                lines.Add(line);
            }
        }

        var output = new List<string>(lines.Count + 2)
        {
            "# Auto-generated by Soenneker.Quark.Gen.Tailwind.BuildTasks",
            "# Combined class names for Tailwind @source to scan."
        };

        output.AddRange(lines);

        string contentsToWrite = string.Join(Environment.NewLine, output) + Environment.NewLine;
        await _fileUtil.Write(destinationPath, contentsToWrite, log: false, cancellationToken: cancellationToken);
    }

    private static void AddManifestPath(List<string> manifestPaths, string? manifestPath)
    {
        if (string.IsNullOrWhiteSpace(manifestPath))
            return;

        string fullPath = Path.GetFullPath(manifestPath);

        if (manifestPaths.Any(path => string.Equals(path, fullPath, StringComparison.OrdinalIgnoreCase)))
            return;

        manifestPaths.Add(fullPath);
    }

    private async ValueTask EnsureInputCss(string tailwindDir, string projectRootForCss, CancellationToken cancellationToken)
    {
        string path = Path.Combine(tailwindDir, "input.css");
        string escapedProjectRoot = projectRootForCss.Replace("\"", "\\\"", StringComparison.Ordinal);

        string contents = @"@import ""tailwindcss"";
@import ""tw-animate-css"";

/* Quark Suite manifest staged locally for Tailwind */
@source ""./quark-tailwind-manifest.txt"";

/* Scan project sources from the consumer project root */
@source ""__QUARK_PROJECT_ROOT__/**/*.{razor,cshtml,html,cs}"";

/* Exclude junk */
@source not ""__QUARK_PROJECT_ROOT__/**/{bin,obj,node_modules,.git}/**"";

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
";
        contents = contents.Replace("__QUARK_PROJECT_ROOT__", escapedProjectRoot, StringComparison.Ordinal);

        if (await _fileUtil.Exists(path, cancellationToken))
        {
            string existing = await _fileUtil.Read(path, log: false, cancellationToken);
            if (string.Equals(existing, contents, StringComparison.Ordinal))
                return;
        }

        // Tailwind v4 syntax (v3 @tailwind directives are deprecated and can cause no output or errors).
        await _fileUtil.Write(path, contents, true, cancellationToken)
                       ;
    }

    private async ValueTask EnsureTailwindConfig(string tailwindDir, CancellationToken cancellationToken)
    {
        string path = Path.Combine(tailwindDir, "tailwind.config.js");
        if (await _fileUtil.Exists(path, cancellationToken))
            return;

        const string content = @"/** @type {import('tailwindcss').Config} */
module.exports = {
  theme: { extend: {} },
  plugins: []
};
";
        await _fileUtil.Write(path, content, log: false, cancellationToken)
                       ;
    }

    private async Task EnsurePackageJson(string tailwindDir, CancellationToken cancellationToken)
    {
        string path = Path.Combine(tailwindDir, "package.json");
        if (await _fileUtil.Exists(path, cancellationToken))
            return;

        const string content = @"{
  ""name"": ""quark-tailwind"",
  ""private"": true,
  ""devDependencies"": {
    ""@tailwindcss/cli"": ""^4.2.2"",
    ""tailwindcss"": ""^4.2.1"",
    ""tw-animate-css"": ""^1.4.0""
  }
}
";
        await _fileUtil.Write(path, content, log: false, cancellationToken)
                       ;
    }

    private async Task<int> RunTailwindCli(string workingDir, string configPath, string inputCss, string outputCssArg, bool minify,
        CancellationToken cancellationToken)
    {
        string inputFileName = Path.GetFileName(inputCss);
        bool hasConfig = await _fileUtil.Exists(configPath, cancellationToken);
        string? configFileName = hasConfig ? Path.GetFileName(configPath) : null;

        var argList = new List<string> { "@tailwindcss/cli" };
        if (hasConfig && !string.IsNullOrEmpty(configFileName))
        {
            argList.Add("-c");
            argList.Add(configFileName);
        }

        argList.Add("-i");
        argList.Add(inputFileName);
        argList.Add("-o");
        argList.Add(outputCssArg);
        if (minify)
            argList.Add("--minify");

        string npxPath = await _nodeUtil.GetNpxPath(cancellationToken);
        var psi = new ProcessStartInfo
        {
            FileName = npxPath,
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string a in argList)
            psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => tcs.TrySetResult(process.ExitCode);

        try
        {
            process.Start();
        }
        catch (Exception e)
        {
            await Console.Error.WriteLineAsync($"Failed to start Tailwind CLI: {e.Message}. Ensure Node is installed.");
            return 1;
        }

        Task<string> outTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> errTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using (cancellationToken.Register(() => tcs.TrySetCanceled()))
        {
            try
            {
                int exit = await tcs.Task;
                string stdout = await outTask;
                string stderr = await errTask;
                if (!string.IsNullOrEmpty(stdout))
                    _logger.LogInformation("{TailwindStdout}", stdout.TrimEnd());
                if (!string.IsNullOrEmpty(stderr))
                    _logger.LogWarning("{TailwindStderr}", stderr.TrimEnd());
                return exit;
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                }

                return 1;
            }
        }
    }

    private static Dictionary<string, string> ParseArgs(string[] args)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i]
                    .StartsWith("--", StringComparison.Ordinal) && i + 1 < args.Length)
            {
                map[args[i]] = args[i + 1];
                i++;
            }
        }

        return map;
    }

    private int Fail(string message)
    {
        var line = $"Soenneker.Quark.Gen.Tailwind.BuildTasks: {message}";
        Console.Error.WriteLine(line);
        Console.WriteLine(line);
        return 1;
    }
}