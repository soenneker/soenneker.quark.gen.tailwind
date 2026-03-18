using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Soenneker.Node.Util.Abstract;
using Soenneker.Quark.Gen.Tailwind.BuildTasks.Abstract;
using Soenneker.Extensions.String;
using Soenneker.Extensions.Task;
using Soenneker.Extensions.ValueTask;
using Soenneker.Utils.Directory.Abstract;
using Soenneker.Utils.File.Abstract;

namespace Soenneker.Quark.Gen.Tailwind.BuildTasks;

/// <inheritdoc cref="ITailwindGeneratorRunner"/>
public sealed class TailwindGeneratorRunner : ITailwindGeneratorRunner
{
    private const string _tailwindDirName = "tailwind";
    private const string _inlineGeneratedTxtFileName = "tw-inline.generated.txt";

    // Regexes for GenerateInlineSourcesFromCsFiles ([TailwindPrefix] + self-referencing Chain properties)
    private static readonly Regex ClassWithAttrRegex = new(
        @"\[(?<attr>[^\]]*TailwindPrefix[^\]]*)\]\s*" +
        @"(?:(?:public|internal|private|protected)\s+)?(?:sealed\s+)?class\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\b(?<after>[^{]*)\{",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex TailwindPrefixArgsRegex = new(
        @"TailwindPrefix\s*\(\s*""(?<prefix>[^""]+)""(?:\s*,\s*Responsive\s*=\s*(?<resp>true|false))?\s*\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex ChainPropRegex = new(
        @"public\s+(?<type>[A-Za-z_][A-Za-z0-9_]*)\s+(?<prop>[A-Za-z_][A-Za-z0-9_]*)\s*=>\s*Chain\s*\(\s*(?<arg>[^)]+)\)\s*;",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex ChainBpPropRegex = new(
        @"public\s+(?<type>[A-Za-z_][A-Za-z0-9_]*)\s+On[A-Za-z0-9_]+\s*=>\s*ChainBp\s*\(\s*BreakpointType\.(?<bp>[A-Za-z0-9_]+)\s*\)\s*;",
        RegexOptions.Compiled | RegexOptions.Singleline);

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

        if (!map.TryGetValue("--projectDir", out string? projectDir) || projectDir.IsNullOrWhiteSpace())
            return Fail("Missing required --projectDir");

        projectDir = Path.GetFullPath(projectDir.Trim()
                                                .Trim('"'));

        var sourceRoots = new List<string> { projectDir };
        if (map.TryGetValue("--sourceDirs", out string? sourceDirs) && !sourceDirs.IsNullOrWhiteSpace())
        {
            foreach (string dir in sourceDirs.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (dir.Length == 0)
                    continue;
                string full = Path.GetFullPath(dir.Trim()
                                                  .Trim('"'));
                if (!sourceRoots.Contains(full))
                    sourceRoots.Add(full);
            }
        }

        // Fallback: include parent of projectDir so we still find source when projectDir is e.g. an empty or wrong path.
        string? projectParent = Path.GetDirectoryName(projectDir);
        if (!string.IsNullOrEmpty(projectParent) && await _directoryUtil.Exists(projectParent, cancellationToken)
                                                                        .NoSync() && !sourceRoots.Contains(projectParent))
            sourceRoots.Add(projectParent);

        Console.WriteLine($"TailwindGenerator: projectDir={projectDir}, sourceRoots={sourceRoots.Count}");

        string tailwindDir = Path.Combine(projectDir, _tailwindDirName);
        await _directoryUtil.Create(tailwindDir, log: false, cancellationToken)
                            .NoSync();

        await EnsureInputCss(tailwindDir, cancellationToken)
            .NoSync();
        await GenerateInlineSourcesFromCsFiles(sourceRoots, tailwindDir, cancellationToken)
            .NoSync();
        await EnsureTailwindConfig(tailwindDir, cancellationToken)
            .NoSync();
        await EnsurePackageJson(tailwindDir, cancellationToken)
            .NoSync();

        try
        {
            await _nodeUtil.NpmInstall(tailwindDir, cleanInstall: false, skipIfUpToDate: true, cancellationToken: cancellationToken)
                           .NoSync();
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
            await _directoryUtil.Create(outputDir, log: false, cancellationToken)
                                .NoSync();

        // Pass path relative to tailwind dir so CLI writes to the correct file (avoids Windows absolute-path issues).
        string outputCssForCli = GetRelativePath(tailwindDir, outputCssFull);

        string inputCss = Path.Combine(tailwindDir, "input.css");
        string configPath = Path.Combine(tailwindDir, "tailwind.config.js");

        int exitCode = await RunTailwindCli(tailwindDir, configPath, inputCss, outputCssForCli, minify: false, cancellationToken)
            .NoSync();
        if (exitCode != 0)
        {
            _logger.LogWarning("Tailwind CLI exited with code {ExitCode}. Ensure Node/npx and @tailwindcss/cli are available.", exitCode);
            return exitCode;
        }

        // Build minified version alongside (quark-tailwind.min.css in same directory).
        string minOutputCssFull = Path.Combine(Path.GetDirectoryName(outputCssFull)!, "quark-tailwind.min.css");
        string? outputDirForMin = Path.GetDirectoryName(minOutputCssFull);
        if (!string.IsNullOrEmpty(outputDirForMin))
            await _directoryUtil.Create(outputDirForMin, log: false, cancellationToken)
                                .NoSync();

        string outputCssForMin = GetRelativePath(tailwindDir, minOutputCssFull);
        exitCode = await RunTailwindCli(tailwindDir, configPath, inputCss, outputCssForMin, minify: true, cancellationToken)
            .NoSync();
        if (exitCode != 0)
        {
            _logger.LogWarning("Tailwind CLI (minify) exited with code {ExitCode}. Full CSS was built; minified output may be missing.", exitCode);
        }

        return 0;
    }

    private static string GetRelativePath(string fromDir, string toPath)
    {
        string rel = Path.GetRelativePath(fromDir, toPath);
        return rel.Replace('\\', '/');
    }

    private static bool IsExcluded(string fullPath)
    {
        string p = fullPath.Replace('\\', '/');
        return p.Contains("/bin/", StringComparison.OrdinalIgnoreCase) || p.Contains("/obj/", StringComparison.OrdinalIgnoreCase) ||
               p.Contains("/node_modules/", StringComparison.OrdinalIgnoreCase) || p.Contains("/.git/", StringComparison.OrdinalIgnoreCase) ||
               p.Contains("/tailwind/", StringComparison.OrdinalIgnoreCase);
    }

    private static string StripComments(string s)
    {
        s = Regex.Replace(s, @"/\*.*?\*/", "", RegexOptions.Singleline);
        s = Regex.Replace(s, @"//.*?$", "", RegexOptions.Multiline);
        return s;
    }

    private static string? TryGetClassBody(string text, int openBraceIndex)
    {
        int depth = 0;
        for (int i = openBraceIndex; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '{')
                depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                    return text.Substring(openBraceIndex + 1, i - openBraceIndex - 1);
            }
        }

        return null;
    }

    private static string? ParseToken(string arg, string propName)
    {
        arg = arg.Trim();
        if (arg.Length >= 2 && arg[0] == '"' && arg[^1] == '"')
            return arg.Substring(1, arg.Length - 2);
        if (arg.Contains('.', StringComparison.Ordinal))
        {
            string lower = propName.ToLowerInvariant();
            if (lower is "inherit" or "initial" or "unset")
                return lower;
            return lower;
        }

        return propName.ToLowerInvariant();
    }

    private async Task GenerateInlineSourcesFromCsFiles(IEnumerable<string> sourceRoots, string tailwindDir, CancellationToken cancellationToken)
    {
        // Output: tailwind/tw-inline.generated.txt (class names for @source to scan)
        string outPath = Path.Combine(tailwindDir, _inlineGeneratedTxtFileName);

        var uniqueLines = new HashSet<string>(StringComparer.Ordinal);
        int totalFilesScanned = 0;
        int tailwindPrefixClasses = 0;

        foreach (string sourceRoot in sourceRoots)
        {
            if (!await _directoryUtil.Exists(sourceRoot, cancellationToken)
                                     .NoSync())
            {
                Console.WriteLine($"TailwindGenerator [inline]: skipping missing source root: {sourceRoot}");
                continue;
            }

            Console.WriteLine($"TailwindGenerator [inline]: scanning source root: {sourceRoot}");
            List<string> csFiles = await _directoryUtil.GetFilesByExtension(sourceRoot, ".cs", recursive: true, cancellationToken)
                                                       .NoSync();
            foreach (string file in csFiles)
            {
                if (IsExcluded(file))
                    continue;
                totalFilesScanned++;
                cancellationToken.ThrowIfCancellationRequested();

                string text;
                try
                {
                    text = await _fileUtil.Read(file, log: false, cancellationToken)
                                          .NoSync();
                }
                catch
                {
                    continue;
                }

                text = StripComments(text);

                foreach (Match m in ClassWithAttrRegex.Matches(text))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    string attrBlob = m.Groups["attr"].Value;
                    var am = TailwindPrefixArgsRegex.Match(attrBlob);
                    if (!am.Success)
                        continue;

                    string prefix = am.Groups["prefix"].Value;
                    bool responsive = true;
                    if (am.Groups["resp"].Success && bool.TryParse(am.Groups["resp"].Value, out bool r))
                        responsive = r;

                    string className = m.Groups["name"].Value;

                    // Find body
                    int braceIdx = m.Index + m.Length - 1; // position of the '{' matched by regex
                    string? body = TryGetClassBody(text, braceIdx);
                    if (body is null)
                        continue;

                    // Collect tokens from Chain(...) properties that return the same type as the class
                    var tokens = new HashSet<string>(StringComparer.Ordinal);

                    foreach (Match pm in ChainPropRegex.Matches(body))
                    {
                        string typeName = pm.Groups["type"].Value;
                        if (!string.Equals(typeName, className, StringComparison.Ordinal))
                            continue;

                        string prop = pm.Groups["prop"].Value;
                        string arg = pm.Groups["arg"].Value;

                        string? token = ParseToken(arg, prop);
                        if (!string.IsNullOrWhiteSpace(token))
                            tokens.Add(token);
                    }

                    if (tokens.Count == 0)
                        continue;

                    // If responsive=true but class doesn't even expose breakpoint properties, still fine:
                    // it just generates responsive variants proactively.
                    // if (responsive && !ChainBpPropRegex.IsMatch(body)) responsive = false;

                    var tokenList = new List<string>(tokens);
                    tokenList.Sort(StringComparer.Ordinal);
                    int added = 0;
                    if (responsive)
                    {
                        foreach (string bp in new[] { "", "sm:", "md:", "lg:", "xl:", "2xl:" })
                        {
                            foreach (string token in tokenList)
                            {
                                uniqueLines.Add(bp + prefix + token);
                                added++;
                            }
                        }
                    }
                    else
                    {
                        foreach (string token in tokenList)
                        {
                            uniqueLines.Add(prefix + token);
                            added++;
                        }
                    }

                    tailwindPrefixClasses += added;
                    Console.WriteLine(
                        $"TailwindGenerator [inline]: [TailwindPrefix] {file} -> class {className}, prefix=\"{prefix}\", responsive={responsive}, tokens=[{string.Join(", ", tokenList)}], lines added={added}");
                }
            }
        }

        // Deterministic output
        var final = new List<string>(uniqueLines);
        final.Sort(StringComparer.Ordinal);

        Console.WriteLine(
            $"TailwindGenerator [inline]: summary: {totalFilesScanned} .cs files scanned, {final.Count} class names (TailwindPrefix={tailwindPrefixClasses})");
        Console.WriteLine($"TailwindGenerator [inline]: output -> {outPath}");
        if (final.Count > 0)
        {
            int sample = Math.Min(15, final.Count);
            var sampleList = new List<string>(sample);
            for (int i = 0; i < sample; i++)
                sampleList.Add(final[i]);
            Console.WriteLine($"TailwindGenerator [inline]: sample classes: [{string.Join(", ", sampleList)}]");
        }

        var sb = new StringBuilder(4096);
        sb.AppendLine("# Auto-generated by Soenneker.Quark.Gen.Tailwind.BuildTasks");
        sb.AppendLine("# Do not edit manually. Class names for Tailwind @source to scan.");

        foreach (string line in final)
            sb.AppendLine(line);

        await _fileUtil.Write(outPath, sb.ToString(), cancellationToken: cancellationToken)
                       .NoSync();
    }

    private async ValueTask EnsureInputCss(string tailwindDir, CancellationToken cancellationToken)
    {
        string path = Path.Combine(tailwindDir, "input.css");
        if (await _fileUtil.Exists(path, cancellationToken))
            return;

        // Tailwind v4 syntax (v3 @tailwind directives are deprecated and can cause no output or errors).
        await _fileUtil.Write(path, @"@import ""tailwindcss"";
@import ""tw-animate-css"";

/* [TailwindPrefix] class names - Tailwind scans this file via @source */
@source ""./tw-inline.generated.txt"";

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
", true, cancellationToken)
                       .NoSync();
    }

    private async ValueTask EnsureTailwindConfig(string tailwindDir, CancellationToken cancellationToken)
    {
        string path = Path.Combine(tailwindDir, "tailwind.config.js");
        if (await _fileUtil.Exists(path, cancellationToken)
                           .NoSync())
            return;

        const string content = @"/** @type {import('tailwindcss').Config} */
module.exports = {
  theme: { extend: {} },
  plugins: []
};
";
        await _fileUtil.Write(path, content, log: false, cancellationToken)
                       .NoSync();
    }

    private async Task EnsurePackageJson(string tailwindDir, CancellationToken cancellationToken)
    {
        string path = Path.Combine(tailwindDir, "package.json");
        if (await _fileUtil.Exists(path, cancellationToken)
                           .NoSync())
            return;

        const string content = @"{
  ""name"": ""quark-tailwind"",
  ""private"": true,
  ""devDependencies"": {
    ""@tailwindcss/cli"": ""^4.2.1"",
    ""tailwindcss"": ""^4.2.1"",
    ""tw-animate-css"": ""^1.4.0""
  }
}
";
        await _fileUtil.Write(path, content, log: false, cancellationToken)
                       .NoSync();
    }

    private async Task<int> RunTailwindCli(string workingDir, string configPath, string inputCss, string outputCssArg, bool minify,
        CancellationToken cancellationToken)
    {
        string inputFileName = Path.GetFileName(inputCss);
        bool hasConfig = await _fileUtil.Exists(configPath, cancellationToken)
                                        .NoSync();
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

        string npxPath = await _nodeUtil.GetNpxPath(cancellationToken)
                                        .NoSync();
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
                int exit = await tcs.Task.NoSync();
                string stdout = await outTask.NoSync();
                string stderr = await errTask.NoSync();
                if (!string.IsNullOrEmpty(stdout))
                    Console.WriteLine(stdout);
                if (!string.IsNullOrEmpty(stderr))
                    await Console.Error.WriteLineAsync(stderr);
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

    private static int Fail(string message)
    {
        var line = $"Soenneker.Quark.Gen.Tailwind.BuildTasks: {message}";
        Console.Error.WriteLine(line);
        Console.WriteLine(line); // Also stdout so MSBuild log shows the reason when Exec captures output
        return 1;
    }
}