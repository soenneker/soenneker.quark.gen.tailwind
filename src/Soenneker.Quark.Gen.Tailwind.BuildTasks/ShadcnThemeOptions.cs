using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Soenneker.Extensions.ValueTask;
using Soenneker.Utils.File.Abstract;
using Soenneker.Utils.PooledStringBuilders;

namespace Soenneker.Quark.Gen.Tailwind.BuildTasks;

internal sealed class ShadcnThemeOptions
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly Dictionary<string, string> _lightOverrides = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _darkOverrides = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _inlineOverrides = new(StringComparer.OrdinalIgnoreCase);

    public string? ConfigPath { get; private set; }

    public string? RawCss { get; private set; }

    public string? CssFilePath { get; private set; }

    public string? Style { get; private set; }

    public string? BaseColor { get; private set; }

    public string? ThemeColor { get; private set; }

    public string? ChartColor { get; private set; }

    public string? Font { get; private set; }

    public string? HeadingFont { get; private set; }

    public string? SerifFont { get; private set; }

    public string? MonoFont { get; private set; }

    public string? Radius { get; private set; }

    public string? Preset { get; private set; }

    public bool IsConfigured { get; private set; }

    public IReadOnlyDictionary<string, string> LightOverrides => _lightOverrides;

    public IReadOnlyDictionary<string, string> DarkOverrides => _darkOverrides;

    public IReadOnlyDictionary<string, string> InlineOverrides => _inlineOverrides;

    public static async ValueTask<ShadcnThemeOptions> Load(string projectDir, string tailwindDir, string defaultConfigFileName,
        IReadOnlyDictionary<string, string> args, IFileUtil fileUtil, ILogger logger, CancellationToken cancellationToken)
    {
        var result = new ShadcnThemeOptions();

        string? explicitConfigPath = GetArg(args, "--shadcnThemeConfig");
        string defaultConfigPath = Path.Combine(tailwindDir, defaultConfigFileName);
        string? configPath = null;

        if (HasValue(explicitConfigPath))
        {
            configPath = ResolvePath(projectDir, explicitConfigPath!);
        }
        else if (await fileUtil.Exists(defaultConfigPath, cancellationToken).NoSync())
        {
            configPath = defaultConfigPath;
        }

        ShadcnThemeConfig? config = null;
        if (HasValue(configPath))
        {
            if (await fileUtil.Exists(configPath!, cancellationToken).NoSync())
            {
                try
                {
                    string json = await fileUtil.Read(configPath!, log: false, cancellationToken);
                    config = JsonSerializer.Deserialize<ShadcnThemeConfig>(json, _jsonOptions);
                    result.ConfigPath = configPath;
                    result.IsConfigured = true;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to read shadcn theme config at {ConfigPath}.", configPath);
                }
            }
            else if (HasValue(explicitConfigPath))
            {
                logger.LogWarning("shadcn theme config was configured but not found at {ConfigPath}.", configPath);
            }
        }

        string configBaseDir = result.ConfigPath is null ? projectDir : Path.GetDirectoryName(result.ConfigPath) ?? projectDir;

        result.RawCss = First(GetArg(args, "--shadcnThemeCss"), config?.Css, config?.RawCss);
        string? cssFilePathArg = GetArg(args, "--shadcnThemeCssFile");
        string? cssFilePath = First(config?.CssFilePath, config?.CssFile);
        result.CssFilePath = HasValue(cssFilePathArg) ? ResolvePath(projectDir, cssFilePathArg!) :
            HasValue(cssFilePath) ? ResolvePath(configBaseDir, cssFilePath!) : null;

        result.Style = First(GetArg(args, "--shadcnThemeStyle"), config?.Style);
        result.BaseColor = First(GetArg(args, "--shadcnThemeBaseColor"), config?.BaseColor, config?.Base);
        result.ThemeColor = First(GetArg(args, "--shadcnThemeColor"), GetArg(args, "--shadcnTheme"), config?.ThemeColor, config?.Theme);
        result.ChartColor = First(GetArg(args, "--shadcnThemeChartColor"), config?.ChartColor, config?.Chart);
        result.Font = First(GetArg(args, "--shadcnThemeFont"), config?.Font, config?.SansFont, config?.SansSerifFont);
        result.HeadingFont = First(GetArg(args, "--shadcnThemeHeadingFont"), config?.HeadingFont);
        result.SerifFont = First(GetArg(args, "--shadcnThemeSerifFont"), config?.SerifFont);
        result.MonoFont = First(GetArg(args, "--shadcnThemeMonoFont"), config?.MonoFont, config?.MonospaceFont);
        result.Radius = First(GetArg(args, "--shadcnThemeRadius"), config?.Radius);
        result.Preset = First(GetArg(args, "--shadcnThemePreset"), config?.Preset);

        ApplyPresetDefaults(result);
        ValidateStyle(result);

        AddVariables(result._lightOverrides, config?.Light);
        AddVariables(result._darkOverrides, config?.Dark);
        AddVariables(result._inlineOverrides, config?.Inline);
        AddVariables(result._inlineOverrides, config?.ThemeVariables);

        if (HasAnyConfiguredValue(result) || HasAnyThemeArg(args))
            result.IsConfigured = true;

        return result;
    }

    private static bool HasAnyConfiguredValue(ShadcnThemeOptions options)
    {
        return HasValue(options.RawCss) || HasValue(options.CssFilePath) || HasValue(options.Style) || HasValue(options.BaseColor) ||
               HasValue(options.ThemeColor) || HasValue(options.ChartColor) || HasValue(options.Font) || HasValue(options.HeadingFont) ||
               HasValue(options.SerifFont) || HasValue(options.MonoFont) || HasValue(options.Radius) || HasValue(options.Preset) ||
               options._lightOverrides.Count > 0 || options._darkOverrides.Count > 0 || options._inlineOverrides.Count > 0;
    }

    private static bool HasAnyThemeArg(IReadOnlyDictionary<string, string> args)
    {
        return HasValue(GetArg(args, "--shadcnThemeConfig")) || HasValue(GetArg(args, "--shadcnThemeCss")) ||
               HasValue(GetArg(args, "--shadcnThemeCssFile")) || HasValue(GetArg(args, "--shadcnThemeStyle")) ||
               HasValue(GetArg(args, "--shadcnThemeBaseColor")) || HasValue(GetArg(args, "--shadcnThemeColor")) ||
               HasValue(GetArg(args, "--shadcnTheme")) || HasValue(GetArg(args, "--shadcnThemeChartColor")) ||
               HasValue(GetArg(args, "--shadcnThemeFont")) || HasValue(GetArg(args, "--shadcnThemeHeadingFont")) ||
               HasValue(GetArg(args, "--shadcnThemeSerifFont")) || HasValue(GetArg(args, "--shadcnThemeMonoFont")) ||
               HasValue(GetArg(args, "--shadcnThemeRadius")) || HasValue(GetArg(args, "--shadcnThemePreset"));
    }

    private static void ApplyPresetDefaults(ShadcnThemeOptions options)
    {
        if (!HasValue(options.Preset))
            return;

        ShadcnPresetConfig? preset = ShadcnPresetDecoder.Decode(options.Preset!);

        if (preset is null)
            throw new InvalidOperationException($"shadcn theme preset \"{options.Preset}\" is not a valid shadcn preset code.");

        options.Style = First(options.Style, preset.Style);
        options.BaseColor = First(options.BaseColor, preset.BaseColor);
        options.ThemeColor = First(options.ThemeColor, preset.Theme);
        options.ChartColor = First(options.ChartColor, preset.ChartColor);
        options.Font = First(options.Font, preset.Font);
        options.HeadingFont = First(options.HeadingFont, preset.FontHeading);
        options.Radius = First(options.Radius, preset.Radius);
    }

    private static void ValidateStyle(ShadcnThemeOptions options)
    {
        if (!HasValue(options.Style))
            return;

        string normalized = NormalizeName(options.Style!);
        if (!ShadcnStyleRegistry.IsSupported(normalized))
            throw new InvalidOperationException($"shadcn style \"{options.Style}\" is not supported. Supported styles: {string.Join(", ", ShadcnStyleRegistry.Names)}.");

        options.Style = normalized;
    }

    private static string? GetArg(IReadOnlyDictionary<string, string> args, string key)
    {
        return args.TryGetValue(key, out string? value) && HasValue(value) ? value.Trim() : null;
    }

    private static string? First(params string?[] values)
    {
        foreach (string? value in values)
        {
            if (HasValue(value))
                return value!.Trim();
        }

        return null;
    }

    private static bool HasValue(string? value) => !string.IsNullOrWhiteSpace(value);

    private static string NormalizeName(string value) => value.Trim().ToLowerInvariant().Replace('_', '-').Replace(' ', '-');

    private static string ResolvePath(string baseDir, string path)
    {
        string trimmed = path.Trim().Trim('"');
        return Path.GetFullPath(Path.IsPathRooted(trimmed) ? trimmed : Path.Combine(baseDir, trimmed));
    }

    private static void AddVariables(IDictionary<string, string> target, IReadOnlyDictionary<string, string>? values)
    {
        if (values is null)
            return;

        foreach ((string key, string value) in values)
        {
            if (!HasValue(key) || !HasValue(value))
                continue;

            target[NormalizeVariableName(key)] = value.Trim();
        }
    }

    internal static string NormalizeVariableName(string key)
    {
        string trimmed = key.Trim();

        while (trimmed.StartsWith("--", StringComparison.Ordinal))
            trimmed = trimmed.Substring(2);

        var builder = new PooledStringBuilder(trimmed.Length + 8);
        try
        {
            for (var i = 0; i < trimmed.Length; i++)
            {
                char current = trimmed[i];

                if (current is '_' or ' ')
                {
                    AppendHyphen(ref builder);
                    continue;
                }

                if (char.IsUpper(current))
                {
                    if (builder.Length > 0 && builder.AsSpan()[^1] != '-')
                        builder.Append('-');

                    builder.Append(char.ToLowerInvariant(current));
                    continue;
                }

                builder.Append(char.ToLowerInvariant(current));
            }

            return builder.ToString().Trim('-');
        }
        finally
        {
            builder.Dispose();
        }
    }

    private static void AppendHyphen(ref PooledStringBuilder builder)
    {
        if (builder.Length == 0 || builder.AsSpan()[^1] == '-')
            return;

        builder.Append('-');
    }

    private sealed class ShadcnThemeConfig
    {
        public string? Css { get; set; }

        public string? RawCss { get; set; }

        public string? CssFilePath { get; set; }

        public string? CssFile { get; set; }

        public string? Style { get; set; }

        public string? BaseColor { get; set; }

        public string? Base { get; set; }

        public string? ThemeColor { get; set; }

        public string? Theme { get; set; }

        public string? ChartColor { get; set; }

        public string? Chart { get; set; }

        public string? Font { get; set; }

        public string? SansFont { get; set; }

        public string? SansSerifFont { get; set; }

        public string? HeadingFont { get; set; }

        public string? SerifFont { get; set; }

        public string? MonoFont { get; set; }

        public string? MonospaceFont { get; set; }

        public string? Radius { get; set; }

        public string? Preset { get; set; }

        public Dictionary<string, string>? Light { get; set; }

        public Dictionary<string, string>? Dark { get; set; }

        public Dictionary<string, string>? Inline { get; set; }

        public Dictionary<string, string>? ThemeVariables { get; set; }
    }
}
