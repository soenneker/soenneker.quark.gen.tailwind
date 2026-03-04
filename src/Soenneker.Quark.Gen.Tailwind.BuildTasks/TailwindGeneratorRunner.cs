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

namespace Soenneker.Quark.Gen.Tailwind.BuildTasks;

/// <inheritdoc cref="ITailwindGeneratorRunner"/>
public sealed class TailwindGeneratorRunner : ITailwindGeneratorRunner
{
    private const string _tailwindDirName = "tailwind";
    private const string _inlineGeneratedTxtFileName = "tw-inline.generated.txt";

    // Regexes for GenerateInlineSourcesFromCsFiles ([TailwindPrefix] / [TailwindSourceInline] + self-referencing Chain properties)
    private static readonly Regex ClassWithAttrRegex = new(
        @"\[(?<attr>[^\]]*TailwindPrefix[^\]]*)\]\s*" +
        @"(?:(?:public|internal|private|protected)\s+)?(?:sealed\s+)?class\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\b(?<after>[^{]*)\{",
        RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex TailwindPrefixArgsRegex = new(
        @"TailwindPrefix\s*\(\s*""(?<prefix>[^""]+)""\s*\)\s*(?:,\s*Responsive\s*=\s*(?<resp>true|false))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex ClassWithTailwindSourceInlineRegex = new(
        @"\[(?<attr>[^\]]*TailwindSourceInline[^\]]*)\]\s*" +
        @"(?:(?:public|internal|private|protected)\s+)?(?:sealed\s+)?class\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\b(?<after>[^{]*)\{",
        RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex TailwindSourceInlineArgsRegex = new(
        @"TailwindSourceInline\s*\(\s*""(?<pattern>[^""]+)""\s*\)\s*(?:,\s*Responsive\s*=\s*(?<resp>true|false))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex ChainPropRegex = new(
        @"public\s+(?<type>[A-Za-z_][A-Za-z0-9_]*)\s+(?<prop>[A-Za-z_][A-Za-z0-9_]*)\s*=>\s*Chain\s*\(\s*(?<arg>[^)]+)\)\s*;",
        RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex ChainBpPropRegex = new(
        @"public\s+(?<type>[A-Za-z_][A-Za-z0-9_]*)\s+On[A-Za-z0-9_]+\s*=>\s*ChainBp\s*\(\s*BreakpointType\.(?<bp>[A-Za-z0-9_]+)\s*\)\s*;",
        RegexOptions.Compiled | RegexOptions.Singleline);

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
        Dictionary<string, string> map = ParseArgs(args);

        if (!map.TryGetValue("--projectDir", out string? projectDir) || projectDir.IsNullOrWhiteSpace())
            return Fail("Missing required --projectDir");

        projectDir = Path.GetFullPath(projectDir.Trim().Trim('"'));

        string tailwindDir = Path.Combine(projectDir, _tailwindDirName);
        if (!Directory.Exists(tailwindDir))
            Directory.CreateDirectory(tailwindDir);

        await EnsureInputCss(tailwindDir, cancellationToken).NoSync();
        await GenerateInlineSourcesFromCsFiles(projectDir, tailwindDir, cancellationToken).ConfigureAwait(false);
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
        if (map.TryGetValue("--tailwindOutput", out string? outPath) && !string.IsNullOrWhiteSpace(outPath))
        {
            outputCssFull = Path.GetFullPath(outPath.Trim().Trim('"'));
        }
        else
        {
            outputCssFull = Path.GetFullPath(Path.Combine(projectDir, "wwwroot", "css", "quark-tailwind.css"));
        }

        string? outputDir = Path.GetDirectoryName(outputCssFull);
        if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
            Directory.CreateDirectory(outputDir);

        // Pass path relative to tailwind dir so CLI writes to the correct file (avoids Windows absolute-path issues).
        string outputCssForCli = GetRelativePath(tailwindDir, outputCssFull);

        string inputCss = Path.Combine(tailwindDir, "input.css");
        string configPath = Path.Combine(tailwindDir, "tailwind.config.js");

        int exitCode = await RunTailwindCli(tailwindDir, configPath, inputCss, outputCssForCli, minify: false, _nodeUtil, cancellationToken).ConfigureAwait(false);
        if (exitCode != 0)
        {
            _logger.LogWarning("Tailwind CLI exited with code {ExitCode}. Ensure Node/npx and @tailwindcss/cli are available.", exitCode);
            return exitCode;
        }

        // Build minified version alongside (quark-tailwind.min.css in same directory).
        string minOutputCssFull = Path.Combine(Path.GetDirectoryName(outputCssFull)!, "quark-tailwind.min.css");
        string? outputDirForMin = Path.GetDirectoryName(minOutputCssFull);
        if (!string.IsNullOrEmpty(outputDirForMin) && !Directory.Exists(outputDirForMin))
            Directory.CreateDirectory(outputDirForMin);

        string outputCssForMin = GetRelativePath(tailwindDir, minOutputCssFull);
        exitCode = await RunTailwindCli(tailwindDir, configPath, inputCss, outputCssForMin, minify: true, _nodeUtil, cancellationToken).ConfigureAwait(false);
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

    private static async Task GenerateInlineSourcesFromCsFiles(string projectDir, string tailwindDir, CancellationToken cancellationToken)
    {
        // Output: tailwind/tw-inline.generated.txt (class names for @source to scan)
        string outPath = Path.Combine(tailwindDir, _inlineGeneratedTxtFileName);

        // ------------------------------------------------------------
        // 1) Enumerate .cs files under projectDir, skipping junk
        // ------------------------------------------------------------
        static bool IsExcluded(string fullPath)
        {
            string p = fullPath.Replace('\\', '/');

            return p.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
                || p.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
                || p.Contains("/node_modules/", StringComparison.OrdinalIgnoreCase)
                || p.Contains("/.git/", StringComparison.OrdinalIgnoreCase)
                || p.Contains("/tailwind/", StringComparison.OrdinalIgnoreCase); // don't scan generated tailwind files
        }

        IEnumerable<string> EnumerateCs(string root)
        {
            var stack = new Stack<string>();
            stack.Push(root);

            while (stack.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string dir = stack.Pop();
                string[] subDirs;
                try { subDirs = Directory.GetDirectories(dir); }
                catch { continue; }

                foreach (string sd in subDirs)
                {
                    if (!IsExcluded(sd))
                        stack.Push(sd);
                }

                string[] files;
                try { files = Directory.GetFiles(dir, "*.cs"); }
                catch { continue; }

                foreach (string f in files)
                {
                    if (!IsExcluded(f))
                        yield return f;
                }
            }
        }

        // ------------------------------------------------------------
        // 2) Regex: find [TailwindPrefix("...")] + class body
        // ------------------------------------------------------------
        // 3) Within class body: self-returning properties that call Chain("token")
        // Strip comments to reduce false positives
        static string StripComments(string s)
        {
            // block comments
            s = Regex.Replace(s, @"/\*.*?\*/", "", RegexOptions.Singleline);
            // line comments
            s = Regex.Replace(s, @"//.*?$", "", RegexOptions.Multiline);
            return s;
        }

        // Extract the full class body starting right after the first '{' of the class.
        static string? TryGetClassBody(string text, int openBraceIndex)
        {
            int depth = 0;
            for (int i = openBraceIndex; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        // body is between first { and matching }
                        return text.Substring(openBraceIndex + 1, i - openBraceIndex - 1);
                    }
                }
            }
            return null;
        }

        static string? ParseToken(string arg, string propName)
        {
            arg = arg.Trim();

            // "auto"
            if (arg.Length >= 2 && arg[0] == '"' && arg[^1] == '"')
                return arg.Substring(1, arg.Length - 2);

            // GlobalKeyword.InheritValue -> inherit (best-effort)
            // If you want a stronger mapping, handle known identifiers here.
            // We can't evaluate non-const values; fall back to propName.
            if (arg.Contains('.', StringComparison.Ordinal))
            {
                // If it looks like Inherit/Initial/Unset by name, use that
                string lower = propName.ToLowerInvariant();
                if (lower is "inherit" or "initial" or "unset")
                    return lower;

                // Otherwise: unknown identifier -> null (skip) or propName
                return lower;
            }

            // Fallback: property name as token (Auto -> auto, etc.)
            return propName.ToLowerInvariant();
        }

        var uniqueLines = new HashSet<string>(StringComparer.Ordinal);

        foreach (string file in EnumerateCs(projectDir))
        {
            cancellationToken.ThrowIfCancellationRequested();

            string text;
            try
            {
                text = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
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

                // Expand to full class names so @source can scan this .txt file
                var tokenList = new List<string>(tokens);
                tokenList.Sort(StringComparer.Ordinal);

                if (responsive)
                {
                    foreach (string bp in new[] { "", "sm:", "md:", "lg:", "xl:", "2xl:" })
                    {
                        foreach (string token in tokenList)
                            uniqueLines.Add(bp + prefix + token);
                    }
                }
                else
                {
                    foreach (string token in tokenList)
                        uniqueLines.Add(prefix + token);
                }
            }

            // [TailwindSourceInline("pattern", Responsive = true/false)]: same pattern — self-referencing Chain/ChainBp → tokens; Responsive → breakpoint prefixes
            foreach (Match m in ClassWithTailwindSourceInlineRegex.Matches(text))
            {
                cancellationToken.ThrowIfCancellationRequested();

                string attrBlob = m.Groups["attr"].Value;
                string className = m.Groups["name"].Value;

                int braceIdx = m.Index + m.Length - 1;
                string? body = TryGetClassBody(text, braceIdx);
                if (body is null)
                    continue;

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

                var tokenList = new List<string>(tokens);
                tokenList.Sort(StringComparer.Ordinal);

                foreach (Match am in TailwindSourceInlineArgsRegex.Matches(attrBlob))
                {
                    string pattern = am.Groups["pattern"].Value;
                    bool responsive = true;
                    if (am.Groups["resp"].Success && bool.TryParse(am.Groups["resp"].Value, out bool r))
                        responsive = r;

                    if (responsive)
                    {
                        foreach (string bp in new[] { "", "sm:", "md:", "lg:", "xl:", "2xl:" })
                        {
                            foreach (string token in tokenList)
                                uniqueLines.Add(bp + pattern + token);
                        }
                    }
                    else
                    {
                        foreach (string token in tokenList)
                            uniqueLines.Add(pattern + token);
                    }
                }
            }
        }

        // Deterministic output
        var final = new List<string>(uniqueLines);
        final.Sort(StringComparer.Ordinal);

        var sb = new StringBuilder(4096);
        sb.AppendLine("# Auto-generated by Soenneker.Quark.Gen.Tailwind.BuildTasks");
        sb.AppendLine("# Do not edit manually. Class names for Tailwind @source to scan.");

        foreach (string line in final)
            sb.AppendLine(line);

        await File.WriteAllTextAsync(outPath, sb.ToString(), cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureInputCss(string tailwindDir, CancellationToken cancellationToken)
    {
        string path = Path.Combine(tailwindDir, "input.css");
        if (File.Exists(path))
            return;

        // Tailwind v4 syntax (v3 @tailwind directives are deprecated and can cause no output or errors).
        await File.WriteAllTextAsync(path, @"@import ""tailwindcss"";
@import ""tw-animate-css"";

/* [TailwindPrefix] / [TailwindSourceInline] class names – Tailwind scans this file via @source */
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
", cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureTailwindConfig(string tailwindDir, CancellationToken cancellationToken)
    {
        string path = Path.Combine(tailwindDir, "tailwind.config.js");
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
        string path = Path.Combine(tailwindDir, "package.json");
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
        string inputFileName = Path.GetFileName(inputCss);
        bool hasConfig = File.Exists(configPath);
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

        string npxPath = await nodeUtil.GetNpxPath(cancellationToken).ConfigureAwait(false);
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
            Console.Error.WriteLine($"Failed to start Tailwind CLI: {e.Message}. Ensure Node is installed.");
            return 1;
        }

        Task<string> outTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> errTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using (cancellationToken.Register(() => tcs.TrySetCanceled()))
        {
            try
            {
                int exit = await tcs.Task.ConfigureAwait(false);
                string stdout = await outTask.ConfigureAwait(false);
                string stderr = await errTask.ConfigureAwait(false);
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