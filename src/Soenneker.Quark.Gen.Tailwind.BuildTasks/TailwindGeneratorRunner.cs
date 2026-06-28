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
using Soenneker.Extensions.ValueTask;
using Soenneker.Hashing.XxHash;

namespace Soenneker.Quark.Gen.Tailwind.BuildTasks;

/// <inheritdoc cref="ITailwindGeneratorRunner"/>
public sealed class TailwindGeneratorRunner : ITailwindGeneratorRunner
{
    private const string _tailwindDirName = "tailwind";
    private const string _inputCssFileName = "input.css";
    private const string _projectManifestFileName = "quark-tailwind-manifest.txt";
    private const string _suiteManifestFileName = "quark-suite-tailwind-manifest.txt";
    private const string _generatedThemeFileName = "quark-theme.generated.css";
    private const string _themeConfigFileName = "quark-shadcn.theme.json";
    private const string _legacyInlineGeneratedTxtFileName = "tw-inline.generated.txt";
    private const string _suitePackageId = "soenneker.quark.suite";
    private const string _defaultThemeConfigJson = """
{
  "baseColor": "Neutral",
  "theme": "Neutral",
  "chartColor": "Neutral",
  "radius": "Default"
}
""";

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

        projectDir = Path.GetFullPath(projectDir.Trim().Trim('"'));

        _logger.LogInformation("Starting Tailwind generation for project {ProjectDir}.", projectDir);

        string tailwindDir = Path.Combine(projectDir, _tailwindDirName);
        _logger.LogInformation("Preparing Tailwind working directory at {TailwindDir}.", tailwindDir);
        await _directoryUtil.Create(tailwindDir, log: false, cancellationToken);

        string projectManifestPath = Path.Combine(tailwindDir, _projectManifestFileName);
        await EnsureLocalManifestFile(projectManifestPath, null, "# Waiting for local project Tailwind manifest generation." + Environment.NewLine,
            cancellationToken);

        _logger.LogInformation("Resolving upstream suite Tailwind manifest for local copy...");
        string? suiteManifestPath = await ResolveManifestPath(projectDir, map, cancellationToken);
        string localSuiteManifestPath = Path.Combine(tailwindDir, _suiteManifestFileName);
        await EnsureLocalManifestFile(localSuiteManifestPath, suiteManifestPath,
            "# No upstream Soenneker.Quark.Suite Tailwind manifest was resolved for this project." + Environment.NewLine, cancellationToken);

        string inputCss = Path.Combine(tailwindDir, _inputCssFileName);
        string generatedThemeCssPath = Path.Combine(tailwindDir, _generatedThemeFileName);
        string themeConfigPath = Path.Combine(tailwindDir, _themeConfigFileName);
        _logger.LogInformation("Ensuring Tailwind input.css, config, and package metadata exist.");

        ShadcnThemeOptions themeOptions;
        bool configuredThemeCss;

        bool explicitShadcnConfiguration = HasExplicitShadcnConfiguration(map);
        bool generatedThemeCssExists = await _fileUtil.Exists(generatedThemeCssPath, cancellationToken);

        if (!explicitShadcnConfiguration && generatedThemeCssExists)
        {
            _logger.LogInformation("Using generated Quark theme token CSS at {ThemeCssPath}.", generatedThemeCssPath);
            themeOptions = new ShadcnThemeOptions();
            configuredThemeCss = true;
        }
        else
        {
            await EnsureDefaultThemeConfig(themeConfigPath, map, cancellationToken);

            themeOptions = await ShadcnThemeOptions.Load(projectDir, tailwindDir, _themeConfigFileName, map, _fileUtil, _logger,
                cancellationToken);
            configuredThemeCss = await EnsureConfiguredThemeCss(generatedThemeCssPath, themeOptions, cancellationToken);
        }

        if (!await _fileUtil.Exists(inputCss, cancellationToken))
        {
            _logger.LogInformation("Project Tailwind input.css not found. Creating starter file at {InputCssPath}.", inputCss);
            string projectRootForCss = GetRelativePath(tailwindDir, projectDir);
            bool generatedThemeExists = await _fileUtil.Exists(generatedThemeCssPath, cancellationToken);

            await EnsureInputCss(inputCss, projectRootForCss, generatedThemeExists, cancellationToken);
        }
        else
        {
            _logger.LogInformation("Using project Tailwind input.css at {InputCssPath}.", inputCss);

            if (configuredThemeCss)
                await EnsureInputCssImportsGeneratedTheme(inputCss, cancellationToken);
        }

        await EnsureTailwindConfig(tailwindDir, cancellationToken);
        await EnsurePackageJson(tailwindDir, cancellationToken);

        string configPath = Path.Combine(tailwindDir, "tailwind.config.js");
        string packageJsonPath = Path.Combine(tailwindDir, "package.json");
        string packageLockPath = Path.Combine(tailwindDir, "package-lock.json");
        string hashPath = Path.Combine(tailwindDir, "tailwind-generator.inputs.hash");

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
        if (!string.IsNullOrEmpty(outputDir))
        {
            _logger.LogInformation("Ensuring Tailwind output directory exists at {OutputDir}.", outputDir);
            await _directoryUtil.Create(outputDir, log: false, cancellationToken);
        }

        string minOutputCssFull = Path.Combine(Path.GetDirectoryName(outputCssFull)!, "quark-tailwind.min.css");
        string inputHash = await ComputeInputHash(projectDir, tailwindDir, projectManifestPath, localSuiteManifestPath, inputCss, generatedThemeCssPath,
            configPath, packageJsonPath, packageLockPath, themeOptions, cancellationToken);

        if (await CanSkipGeneration(inputHash, hashPath, outputCssFull, minOutputCssFull, cancellationToken))
        {
            _logger.LogInformation("Tailwind inputs unchanged. Skipping npm install and Tailwind CLI for project {ProjectDir}.", projectDir);
            return 0;
        }

        try
        {
            _logger.LogInformation("Running npm install in {TailwindDir}.", tailwindDir);
            await _nodeUtil.NpmInstall(tailwindDir, cleanInstall: false, skipIfUpToDate: true, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "npm install failed. Continuing with Tailwind CLI.");
        }

        // Pass path relative to tailwind dir so CLI writes to the correct file (avoids Windows absolute-path issues).
        string outputCssForCli = GetRelativePath(tailwindDir, outputCssFull);

        _logger.LogInformation("Running Tailwind CLI for full CSS output at {OutputCss}.", outputCssFull);
        int exitCode = await RunTailwindCli(tailwindDir, configPath, inputCss, outputCssForCli, minify: false, cancellationToken);
        if (exitCode != 0)
        {
            _logger.LogWarning("Tailwind CLI exited with code {ExitCode}. Ensure Node/npx and @tailwindcss/cli are available.", exitCode);
            return exitCode;
        }

        // Build minified version alongside (quark-tailwind.min.css in same directory).
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

        await _fileUtil.Write(hashPath, inputHash, log: false, cancellationToken);
        _logger.LogInformation("Completed Tailwind generation for project {ProjectDir}.", projectDir);
        return 0;
    }

    private async ValueTask EnsureDefaultThemeConfig(string themeConfigPath, IReadOnlyDictionary<string, string> args, CancellationToken cancellationToken)
    {
        if (args.TryGetValue("--shadcnThemeConfig", out string? explicitConfigPath) && !string.IsNullOrWhiteSpace(explicitConfigPath))
            return;

        if (await _fileUtil.Exists(themeConfigPath, cancellationToken))
            return;

        await _fileUtil.Write(themeConfigPath, _defaultThemeConfigJson + Environment.NewLine, log: false, cancellationToken);
        _logger.LogInformation("Created default shadcn theme config at {ThemeConfigPath}.", themeConfigPath);
    }

    private async ValueTask<bool> CanSkipGeneration(string inputHash, string hashPath, string outputCssPath, string minOutputCssPath,
        CancellationToken cancellationToken)
    {
        bool hasOutput = await _fileUtil.Exists(outputCssPath, cancellationToken);
        bool hasMinOutput = await _fileUtil.Exists(minOutputCssPath, cancellationToken);
        bool hasHash = await _fileUtil.Exists(hashPath, cancellationToken);

        if (!hasOutput || !hasMinOutput || !hasHash)
        {
            _logger.LogDebug("Tailwind cache miss because required files are missing. css={HasOutput} min={HasMinOutput} hash={HasHash}", hasOutput,
                hasMinOutput, hasHash);
            return false;
        }

        string? previousHash = await _fileUtil.TryRead(hashPath, log: false, cancellationToken);
        bool isMatch = string.Equals(previousHash?.Trim(), inputHash, StringComparison.Ordinal);

        if (!isMatch)
        {
            _logger.LogDebug("Tailwind cache miss because the input hash changed. previous={PreviousHash} current={CurrentHash}", previousHash?.Trim(),
                inputHash);
        }

        return isMatch;
    }

    private async ValueTask<bool> EnsureConfiguredThemeCss(string generatedThemeCssPath, ShadcnThemeOptions themeOptions,
        CancellationToken cancellationToken)
    {
        if (!themeOptions.IsConfigured)
            return false;

        string css;

        if (!string.IsNullOrWhiteSpace(themeOptions.RawCss))
        {
            css = themeOptions.RawCss!;
        }
        else if (!string.IsNullOrWhiteSpace(themeOptions.CssFilePath))
        {
            if (!await _fileUtil.Exists(themeOptions.CssFilePath!, cancellationToken).NoSync())
                throw new FileNotFoundException("shadcn theme CSS file was configured but not found.", themeOptions.CssFilePath);

            css = await _fileUtil.Read(themeOptions.CssFilePath!, log: false, cancellationToken);
        }
        else
        {
            css = ShadcnThemeCssGenerator.Generate(themeOptions);
        }

        if (string.IsNullOrWhiteSpace(css))
            throw new InvalidOperationException("shadcn theme configuration produced no CSS.");

        return await WriteThemeCss(generatedThemeCssPath, css, "Configured shadcn theme CSS", cancellationToken);
    }

    private async ValueTask<bool> WriteThemeCss(string generatedThemeCssPath, string css, string sourceDescription, CancellationToken cancellationToken)
    {
        string normalizedCss = css.TrimEnd() + Environment.NewLine;
        string? existing = await _fileUtil.TryRead(generatedThemeCssPath, log: false, cancellationToken);

        if (string.Equals(existing, normalizedCss, StringComparison.Ordinal))
        {
            _logger.LogInformation("{SourceDescription} is already up-to-date at {ThemeCssPath}.", sourceDescription, generatedThemeCssPath);
            return true;
        }

        string? outputDir = Path.GetDirectoryName(generatedThemeCssPath);
        if (!string.IsNullOrWhiteSpace(outputDir))
            await _directoryUtil.Create(outputDir, log: false, cancellationToken).NoSync();

        await _fileUtil.Write(generatedThemeCssPath, normalizedCss, log: false, cancellationToken);
        _logger.LogInformation("Wrote {SourceDescription} to {ThemeCssPath}.", sourceDescription, generatedThemeCssPath);
        return true;
    }

    private async ValueTask EnsureInputCssImportsGeneratedTheme(string inputCssPath, CancellationToken cancellationToken)
    {
        string contents = await _fileUtil.Read(inputCssPath, log: false, cancellationToken);

        if (contents.Contains(_generatedThemeFileName, StringComparison.OrdinalIgnoreCase))
            return;

        string importBlock = $"@import \"./{_generatedThemeFileName}\";{Environment.NewLine}{Environment.NewLine}";

        foreach (string fallbackThemeBlock in GetKnownFallbackThemeBlocks())
        {
            if (contents.Contains(fallbackThemeBlock, StringComparison.Ordinal))
            {
                string updated = contents.Replace(fallbackThemeBlock, importBlock, StringComparison.Ordinal);
                await _fileUtil.Write(inputCssPath, updated, log: false, cancellationToken);
                _logger.LogInformation("Updated Tailwind input.css to import configured theme CSS.");
                return;
            }
        }

        _logger.LogWarning(
            "Configured shadcn theme CSS was generated, but {InputCssPath} does not import {ThemeFileName}. Add @import \"./{ThemeFileName}\" near the top of input.css.",
            inputCssPath, _generatedThemeFileName, _generatedThemeFileName);
    }

    private async ValueTask<string> ComputeInputHash(string projectDir, string tailwindDir, string projectManifestPath, string localSuiteManifestPath,
        string inputCssPath, string generatedThemeCssPath, string configPath, string packageJsonPath, string packageLockPath,
        ShadcnThemeOptions themeOptions,
        CancellationToken cancellationToken)
    {
        var entries = new List<string>();

        await AddSourceMetadataEntries(entries, projectDir, ".cs", cancellationToken);
        await AddSourceMetadataEntries(entries, projectDir, ".razor", cancellationToken);
        await AddSourceMetadataEntries(entries, projectDir, ".cshtml", cancellationToken);
        await AddSourceMetadataEntries(entries, projectDir, ".html", cancellationToken);

        await AddSpecificFileMetadata(entries, tailwindDir, projectManifestPath, "manifest-project", cancellationToken);
        await AddSpecificFileMetadata(entries, tailwindDir, localSuiteManifestPath, "manifest-suite", cancellationToken);
        await AddSpecificFileMetadata(entries, tailwindDir, inputCssPath, "input-css", cancellationToken);
        await AddSpecificFileMetadata(entries, tailwindDir, generatedThemeCssPath, "generated-theme", cancellationToken);
        await AddSpecificFileMetadata(entries, tailwindDir, configPath, "tailwind-config", cancellationToken);
        await AddSpecificFileMetadata(entries, tailwindDir, packageJsonPath, "package-json", cancellationToken);
        await AddSpecificFileMetadata(entries, tailwindDir, packageLockPath, "package-lock", cancellationToken);

        if (!string.IsNullOrWhiteSpace(themeOptions.ConfigPath))
            await AddSpecificFileMetadata(entries, projectDir, themeOptions.ConfigPath!, "theme-config", cancellationToken);

        if (!string.IsNullOrWhiteSpace(themeOptions.CssFilePath))
            await AddSpecificFileMetadata(entries, projectDir, themeOptions.CssFilePath!, "theme-css-file", cancellationToken);

        AddThemeOptionMetadata(entries, themeOptions);

        string assemblyLocation = GetType().Assembly.Location;
        if (!string.IsNullOrWhiteSpace(assemblyLocation) && await _fileUtil.Exists(assemblyLocation, cancellationToken).NoSync())
        {
            entries.Add(BuildMetadataEntry("buildtasks", assemblyLocation, assemblyLocation));
        }

        entries.Sort(StringComparer.Ordinal);

        string manifest = string.Join('\n', entries);
        return XxHash3Util.Hash(manifest);
    }

    private async ValueTask AddSourceMetadataEntries(List<string> entries, string projectDir, string extension, CancellationToken cancellationToken)
    {
        List<string> files = await _directoryUtil.GetFilesByExtension(projectDir, extension, recursive: true, cancellationToken).NoSync();

        foreach (string file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (IsExcludedSourcePath(file))
                continue;

            entries.Add(BuildMetadataEntry(projectDir, file, extension));
        }
    }

    private async ValueTask AddSpecificFileMetadata(List<string> entries, string rootDir, string filePath, string category, CancellationToken cancellationToken)
    {
        if (!await _fileUtil.Exists(filePath, cancellationToken).NoSync())
            return;

        entries.Add(BuildMetadataEntry(rootDir, filePath, category));
    }

    private static void AddThemeOptionMetadata(List<string> entries, ShadcnThemeOptions options)
    {
        if (!options.IsConfigured)
            return;

        AddThemeOptionEntry(entries, "theme-style", options.Style);
        AddThemeOptionEntry(entries, "theme-base-color", options.BaseColor);
        AddThemeOptionEntry(entries, "theme-color", options.ThemeColor);
        AddThemeOptionEntry(entries, "theme-chart-color", options.ChartColor);
        AddThemeOptionEntry(entries, "theme-font", options.Font);
        AddThemeOptionEntry(entries, "theme-heading-font", options.HeadingFont);
        AddThemeOptionEntry(entries, "theme-serif-font", options.SerifFont);
        AddThemeOptionEntry(entries, "theme-mono-font", options.MonoFont);
        AddThemeOptionEntry(entries, "theme-radius", options.Radius);
        AddThemeOptionEntry(entries, "theme-preset", options.Preset);
        AddThemeDictionaryEntries(entries, "theme-light", options.LightOverrides);
        AddThemeDictionaryEntries(entries, "theme-dark", options.DarkOverrides);
        AddThemeDictionaryEntries(entries, "theme-inline", options.InlineOverrides);
    }

    private static void AddThemeOptionEntry(List<string> entries, string category, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        entries.Add($"{category}|{value.Trim()}");
    }

    private static void AddThemeDictionaryEntries(List<string> entries, string category, IReadOnlyDictionary<string, string> values)
    {
        foreach ((string key, string value) in values.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                continue;

            entries.Add($"{category}|{key.Trim()}|{value.Trim()}");
        }
    }

    private static string BuildMetadataEntry(string rootDir, string filePath, string category)
    {
        var info = new FileInfo(filePath);
        string relativePath = Path.GetRelativePath(rootDir, filePath).Replace('\\', '/');
        return $"{category}|{relativePath}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
    }

    private static bool IsExcludedSourcePath(string path)
    {
        return path.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase) || path.Contains("/obj/", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("\\bin\\", StringComparison.OrdinalIgnoreCase) || path.Contains("/bin/", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("\\node_modules\\", StringComparison.OrdinalIgnoreCase) || path.Contains("/node_modules/", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("\\.git\\", StringComparison.OrdinalIgnoreCase) || path.Contains("/.git/", StringComparison.OrdinalIgnoreCase);
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

    private async ValueTask EnsureLocalManifestFile(string destinationPath, string? sourcePath, string placeholderContents, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(sourcePath))
        {
            string normalizedSourcePath = Path.GetFullPath(sourcePath.Trim().Trim('"'));

            if (string.Equals(normalizedSourcePath, Path.GetFullPath(destinationPath), StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Using local Tailwind manifest already present at {ManifestPath}.", destinationPath);
                return;
            }

            if (await _fileUtil.Exists(normalizedSourcePath, cancellationToken))
            {
                string sourceContents = await _fileUtil.Read(normalizedSourcePath, log: false, cancellationToken);
                string? existingContents = await _fileUtil.TryRead(destinationPath, log: false, cancellationToken);

                if (string.Equals(existingContents, sourceContents, StringComparison.Ordinal))
                {
                    _logger.LogInformation("Tailwind manifest at {DestinationPath} is already up-to-date.", destinationPath);
                    return;
                }

                await _fileUtil.Write(destinationPath, sourceContents, log: false, cancellationToken);
                _logger.LogInformation("Copied Tailwind manifest from {SourcePath} to {DestinationPath}.", normalizedSourcePath, destinationPath);
                return;
            }
        }

        if (await _fileUtil.Exists(destinationPath, cancellationToken))
            return;

        await _fileUtil.Write(destinationPath, placeholderContents, log: false, cancellationToken);
        _logger.LogInformation("Created placeholder Tailwind manifest at {ManifestPath}.", destinationPath);
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

        IEnumerable<string?> includes = document.Descendants().Where(element => element.Name.LocalName == "ProjectReference")
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

            string manifestPath = Path.Combine(referenceDirectory, _tailwindDirName, _suiteManifestFileName);
            if (await _fileUtil.Exists(manifestPath, cancellationToken))
                return manifestPath;

            string legacyManifestPath = Path.Combine(referenceDirectory, _tailwindDirName, _legacyInlineGeneratedTxtFileName);
            if (await _fileUtil.Exists(legacyManifestPath, cancellationToken))
                return legacyManifestPath;
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

            string[] suiteLibraries = libraries.EnumerateObject().Select(property => property.Name)
                                               .Where(name => name.StartsWith(_suitePackageId + "/", StringComparison.OrdinalIgnoreCase)).ToArray();

            if (suiteLibraries.Length == 0)
                return null;

            string[] folders = packageFolders.EnumerateObject()
                                             .Select(property => property.Name.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).ToArray();

            foreach (string library in suiteLibraries)
            {
                int separatorIndex = library.IndexOf('/');
                if (separatorIndex < 0 || separatorIndex == library.Length - 1)
                    continue;

                string version = library.Substring(separatorIndex + 1);

                foreach (string folder in folders)
                {
                    string packageManifestPath = Path.Combine(folder, _suitePackageId, version, _tailwindDirName, _suiteManifestFileName);

                    if (await _fileUtil.Exists(packageManifestPath, cancellationToken))
                        return packageManifestPath;

                    string contentFilesManifestPath = Path.Combine(folder, _suitePackageId, version, "contentFiles", "any", "any", "tailwind",
                        _suiteManifestFileName);

                    if (await _fileUtil.Exists(contentFilesManifestPath, cancellationToken))
                        return contentFilesManifestPath;

                    string legacyManifestPath = Path.Combine(folder, _suitePackageId, version, _tailwindDirName, _legacyInlineGeneratedTxtFileName);
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

    private static string? GetProjectFilePath(string projectDir)
    {
        string[] projectFiles = Directory.GetFiles(projectDir, "*.csproj", SearchOption.TopDirectoryOnly);
        if (projectFiles.Length == 0)
            return null;

        if (projectFiles.Length == 1)
            return projectFiles[0];

        string directoryName = Path.GetFileName(projectDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return projectFiles.FirstOrDefault(path => string.Equals(Path.GetFileNameWithoutExtension(path), directoryName, StringComparison.OrdinalIgnoreCase)) ??
               projectFiles[0];
    }

    private async ValueTask EnsureInputCss(string inputCssPath, string projectRootForCss, bool generatedThemeExists, CancellationToken cancellationToken)
    {
        string escapedProjectRoot = projectRootForCss.Replace("\"", "\\\"", StringComparison.Ordinal);
        string sourceBlock =
            $"@source \"./{_suiteManifestFileName}\";{Environment.NewLine}@source \"./{_projectManifestFileName}\";{Environment.NewLine}{Environment.NewLine}";
        string themeBlock = generatedThemeExists
            ? $"@import \"./{_generatedThemeFileName}\";{Environment.NewLine}{Environment.NewLine}"
            : GetCurrentFallbackThemeBlock();

        string contents = @"@import ""tailwindcss"";
@import ""tw-animate-css"";

__QUARK_THEME_BLOCK____QUARK_MANIFEST_SOURCES__/* Scan project sources from the consumer project root */
@source ""__QUARK_PROJECT_ROOT__/**/*.{razor,cshtml,html,cs}"";

/* Exclude junk */
@source not ""__QUARK_PROJECT_ROOT__/**/{bin,obj,node_modules,.git}/**"";

@custom-variant dark (&:is(.dark *));

@layer base {
  * {
    @apply border-border outline-ring/50;
  }
  body {
    @apply bg-background text-foreground;
  }
}
";
        contents = contents.Replace("__QUARK_THEME_BLOCK__", themeBlock, StringComparison.Ordinal)
                           .Replace("__QUARK_MANIFEST_SOURCES__", sourceBlock, StringComparison.Ordinal)
                           .Replace("__QUARK_PROJECT_ROOT__", escapedProjectRoot, StringComparison.Ordinal);

        // Tailwind v4 syntax (v3 @tailwind directives are deprecated and can cause no output or errors).
        await _fileUtil.Write(inputCssPath, contents, true, cancellationToken);
    }

    private static IEnumerable<string> GetKnownFallbackThemeBlocks()
    {
        yield return GetCurrentFallbackThemeBlock();
        yield return GetFallbackThemeBlock();
    }

    private static string GetCurrentFallbackThemeBlock()
    {
        return ShadcnThemeCssGenerator.Generate(new ShadcnThemeOptions()) + Environment.NewLine + Environment.NewLine;
    }

    private static string GetFallbackThemeBlock()
    {
        return @":root {
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

";
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
        await _fileUtil.Write(path, content, log: false, cancellationToken);
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
        await _fileUtil.Write(path, content, log: false, cancellationToken);
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

    private static bool HasExplicitShadcnConfiguration(IReadOnlyDictionary<string, string> args)
    {
        return HasArg(args, "--shadcnThemeConfig") ||
               HasArg(args, "--shadcnThemeCss") ||
               HasArg(args, "--shadcnThemeCssFile") ||
               HasArg(args, "--shadcnThemeStyle") ||
               HasArg(args, "--shadcnThemeBaseColor") ||
               HasArg(args, "--shadcnThemeColor") ||
               HasArg(args, "--shadcnTheme") ||
               HasArg(args, "--shadcnThemeChartColor") ||
               HasArg(args, "--shadcnThemeFont") ||
               HasArg(args, "--shadcnThemeHeadingFont") ||
               HasArg(args, "--shadcnThemeSerifFont") ||
               HasArg(args, "--shadcnThemeMonoFont") ||
               HasArg(args, "--shadcnThemeRadius") ||
               HasArg(args, "--shadcnThemePreset");
    }

    private static bool HasArg(IReadOnlyDictionary<string, string> args, string key) => !string.IsNullOrWhiteSpace(GetArg(args, key));

    private static string? GetArg(IReadOnlyDictionary<string, string> args, string key)
    {
        return args.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
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

    private int Fail(string message)
    {
        var line = $"Soenneker.Quark.Gen.Tailwind.BuildTasks: {message}";
        Console.Error.WriteLine(line);
        Console.WriteLine(line);
        return 1;
    }
}
