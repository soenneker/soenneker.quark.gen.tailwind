using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Soenneker.Node.Util.Abstract;
using Soenneker.Quark.Gen.Tailwind.BuildTasks.Abstract;

namespace Soenneker.Quark.Gen.Tailwind.BuildTasks;

/// <inheritdoc cref="ITailwindGeneratorRunner"/>
public sealed class TailwindGeneratorRunner : ITailwindGeneratorRunner
{
    private const string TailwindDirName = "tailwind";

    private readonly ILogger<TailwindGeneratorRunner> _logger;
    private readonly INodeUtil _nodeUtil;

    public TailwindGeneratorRunner(ILogger<TailwindGeneratorRunner> logger, INodeUtil nodeUtil)
    {
        _logger = logger;
        _nodeUtil = nodeUtil;
    }

    public async ValueTask<int> Run(CancellationToken cancellationToken = default)
    {
        string[] args = Environment.GetCommandLineArgs();
        var map = ParseArgs(args);

        if (!map.TryGetValue("--projectDir", out var projectDir) || string.IsNullOrWhiteSpace(projectDir))
            return Fail("Missing required --projectDir");

        projectDir = Path.GetFullPath(projectDir.Trim().Trim('"'));

        var tailwindDir = Path.Combine(projectDir, TailwindDirName);
        if (!Directory.Exists(tailwindDir))
            Directory.CreateDirectory(tailwindDir);

        await EnsureInputCss(tailwindDir, cancellationToken).ConfigureAwait(false);
        await EnsureTailwindConfig(tailwindDir, cancellationToken).ConfigureAwait(false);
        await EnsurePackageJson(tailwindDir, cancellationToken).ConfigureAwait(false);

        try
        {
            await _nodeUtil.NpmInstall(tailwindDir, cleanInstall: false, skipIfUpToDate: true, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "npm install failed. Continuing with Tailwind CLI.");
        }

        // Resolve output path: prefer --tailwindOutput, else projectDir/wwwroot/css/quark-tailwind.css (no ambiguity).
        string outputCssFull;
        if (map.TryGetValue("--tailwindOutput", out var outPath) && !string.IsNullOrWhiteSpace(outPath))
        {
            outputCssFull = Path.GetFullPath(outPath.Trim().Trim('"'));
        }
        else
        {
            outputCssFull = Path.GetFullPath(Path.Combine(projectDir, "wwwroot", "css", "quark-tailwind.css"));
        }

        var outputDir = Path.GetDirectoryName(outputCssFull);
        if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
            Directory.CreateDirectory(outputDir);

        // Pass path relative to tailwind dir so CLI writes to the correct file (avoids Windows absolute-path issues).
        string outputCssForCli = GetRelativePath(tailwindDir, outputCssFull);

        var inputCss = Path.Combine(tailwindDir, "input.css");
        var configPath = Path.Combine(tailwindDir, "tailwind.config.js");

        int exitCode = await RunTailwindCli(tailwindDir, configPath, inputCss, outputCssForCli, minify: false, _nodeUtil, cancellationToken).ConfigureAwait(false);
        if (exitCode != 0)
        {
            _logger.LogWarning("Tailwind CLI exited with code {ExitCode}. Ensure Node/npx and @tailwindcss/cli are available.", exitCode);
            return exitCode;
        }

        // Build minified version alongside (quark-tailwind.min.css in same directory).
        var minOutputCssFull = Path.Combine(Path.GetDirectoryName(outputCssFull)!, "quark-tailwind.min.css");
        var outputDirForMin = Path.GetDirectoryName(minOutputCssFull);
        if (!string.IsNullOrEmpty(outputDirForMin) && !Directory.Exists(outputDirForMin))
            Directory.CreateDirectory(outputDirForMin);
        var outputCssForMin = GetRelativePath(tailwindDir, minOutputCssFull);
        exitCode = await RunTailwindCli(tailwindDir, configPath, inputCss, outputCssForMin, minify: true, _nodeUtil, cancellationToken).ConfigureAwait(false);
        if (exitCode != 0)
        {
            _logger.LogWarning("Tailwind CLI (minify) exited with code {ExitCode}. Full CSS was built; minified output may be missing.", exitCode);
        }

        return 0;
    }

    private static string GetRelativePath(string fromDir, string toPath)
    {
        var rel = Path.GetRelativePath(fromDir, toPath);
        return rel.Replace('\\', '/');
    }

    private static async Task EnsureInputCss(string tailwindDir, CancellationToken cancellationToken)
    {
        var path = Path.Combine(tailwindDir, "input.css");
        if (File.Exists(path))
            return;
        // Tailwind v4 syntax (v3 @tailwind directives are deprecated and can cause no output or errors).
        await File.WriteAllTextAsync(path, @"@import ""tailwindcss"";
@import ""tw-animate-css"";
 
/* Scan everything one level up */
@source ""../**/*.{razor,cshtml,html,cs}"";

/* Exclude junk */
@source not ""../**/{bin,obj,node_modules,.git}/**"";

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
", cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureTailwindConfig(string tailwindDir, CancellationToken cancellationToken)
    {
        var path = Path.Combine(tailwindDir, "tailwind.config.js");
        if (File.Exists(path))
            return;
        const string content = @"/** @type {import('tailwindcss').Config} */
module.exports = {
  theme: { extend: {} },
  plugins: []
};
";
        await File.WriteAllTextAsync(path, content, cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsurePackageJson(string tailwindDir, CancellationToken cancellationToken)
    {
        var path = Path.Combine(tailwindDir, "package.json");
        if (File.Exists(path))
            return;
        const string content = @"{
  ""name"": ""quark-tailwind"",
  ""private"": true,
  ""devDependencies"": {
    ""@tailwindcss/cli"": ""^4.0.0"",
    ""tailwindcss"": ""^4.0.0"",
    ""tw-animate-css"": ""^1.0.0""
  }
}
";
        await File.WriteAllTextAsync(path, content, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> RunTailwindCli(string workingDir, string configPath, string inputCss, string outputCssArg, bool minify, INodeUtil nodeUtil, CancellationToken cancellationToken)
    {
        var inputFileName = Path.GetFileName(inputCss);
        var hasConfig = File.Exists(configPath);
        var configFileName = hasConfig ? Path.GetFileName(configPath) : null;

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

        var npxPath = await nodeUtil.GetNpxPath(cancellationToken).ConfigureAwait(false);
        var psi = new ProcessStartInfo
        {
            FileName = npxPath,
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var a in argList)
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
            Console.Error.WriteLine($"Failed to start Tailwind CLI: {e.Message}. Ensure Node is installed.");
            return 1;
        }

        var outTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using (cancellationToken.Register(() => tcs.TrySetCanceled()))
        {
            try
            {
                int exit = await tcs.Task.ConfigureAwait(false);
                var stdout = await outTask.ConfigureAwait(false);
                var stderr = await errTask.ConfigureAwait(false);
                if (!string.IsNullOrEmpty(stdout))
                    Console.WriteLine(stdout);
                if (!string.IsNullOrEmpty(stderr))
                    Console.Error.WriteLine(stderr);
                return exit;
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return 1;
            }
        }
    }

    private static Dictionary<string, string> ParseArgs(string[] args)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal) && i + 1 < args.Length)
            {
                map[args[i]] = args[i + 1];
                i++;
            }
        }
        return map;
    }

    private static int Fail(string message)
    {
        var line = $"Soenneker.Quark.Gen.Tailwind.BuildTasks: {message}";
        Console.Error.WriteLine(line);
        Console.WriteLine(line); // Also stdout so MSBuild log shows the reason when Exec captures output
        return 1;
    }
}
