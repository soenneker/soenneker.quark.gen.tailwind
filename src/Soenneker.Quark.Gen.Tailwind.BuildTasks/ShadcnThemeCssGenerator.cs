using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Soenneker.Quark.Gen.Tailwind.BuildTasks;

internal static class ShadcnThemeCssGenerator
{
    private const string _defaultSansFallback =
        "ui-sans-serif, system-ui, -apple-system, BlinkMacSystemFont, \"Segoe UI\", sans-serif, \"Apple Color Emoji\", \"Segoe UI Emoji\", \"Segoe UI Symbol\", \"Noto Color Emoji\"";

    private const string _defaultSerifFallback = "ui-serif, Georgia, Cambria, \"Times New Roman\", Times, serif";

    private const string _defaultMonoFallback =
        "ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, \"Liberation Mono\", \"Courier New\", monospace";

    private static readonly string[] _chartVariableNames = ["chart-1", "chart-2", "chart-3", "chart-4", "chart-5"];

    private static readonly string[] _schemeVariableOrder =
    [
        "background", "foreground", "card", "card-foreground", "popover", "popover-foreground", "primary", "primary-foreground",
        "secondary", "secondary-foreground", "muted", "muted-foreground", "accent", "accent-foreground", "destructive",
        "border", "input", "ring", "chart-1", "chart-2", "chart-3", "chart-4", "chart-5", "series-1", "series-2", "series-3",
        "series-4", "series-5", "radius", "sidebar", "sidebar-foreground", "sidebar-primary", "sidebar-primary-foreground",
        "sidebar-accent", "sidebar-accent-foreground", "sidebar-border", "sidebar-ring", "font-sans", "font-heading", "font-serif",
        "font-mono"
    ];

    private static readonly string[] _inlineVariableOrder =
    [
        "font-sans", "font-heading", "font-mono", "font-serif", "color-background", "color-foreground", "color-card",
        "color-card-foreground", "color-popover", "color-popover-foreground", "color-primary", "color-primary-foreground",
        "color-secondary", "color-secondary-foreground", "color-muted", "color-muted-foreground", "color-accent",
        "color-accent-foreground", "color-destructive", "color-border", "color-input", "color-ring", "color-chart-1", "color-chart-2",
        "color-chart-3", "color-chart-4", "color-chart-5", "color-series-1", "color-series-2", "color-series-3", "color-series-4",
        "color-series-5", "color-sidebar", "color-sidebar-foreground", "color-sidebar-primary", "color-sidebar-primary-foreground",
        "color-sidebar-accent", "color-sidebar-accent-foreground", "color-sidebar-border", "color-sidebar-ring", "radius-sm", "radius-md",
        "radius-lg", "radius-xl", "radius-2xl", "radius-3xl", "radius-4xl"
    ];

    private static readonly Dictionary<string, FontDefinition> _fontDefinitions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["geist"] = new("'Geist Variable', sans-serif", "--font-sans"),
        ["inter"] = new("'Inter Variable', sans-serif", "--font-sans"),
        ["noto-sans"] = new("'Noto Sans Variable', sans-serif", "--font-sans"),
        ["nunito-sans"] = new("'Nunito Sans Variable', sans-serif", "--font-sans"),
        ["figtree"] = new("'Figtree Variable', sans-serif", "--font-sans"),
        ["roboto"] = new("'Roboto Variable', sans-serif", "--font-sans"),
        ["raleway"] = new("'Raleway Variable', sans-serif", "--font-sans"),
        ["dm-sans"] = new("'DM Sans Variable', sans-serif", "--font-sans"),
        ["public-sans"] = new("'Public Sans Variable', sans-serif", "--font-sans"),
        ["outfit"] = new("'Outfit Variable', sans-serif", "--font-sans"),
        ["oxanium"] = new("'Oxanium Variable', sans-serif", "--font-sans"),
        ["manrope"] = new("'Manrope Variable', sans-serif", "--font-sans"),
        ["space-grotesk"] = new("'Space Grotesk Variable', sans-serif", "--font-sans"),
        ["montserrat"] = new("'Montserrat Variable', sans-serif", "--font-sans"),
        ["ibm-plex-sans"] = new("'IBM Plex Sans Variable', sans-serif", "--font-sans"),
        ["source-sans-3"] = new("'Source Sans 3 Variable', sans-serif", "--font-sans"),
        ["instrument-sans"] = new("'Instrument Sans Variable', sans-serif", "--font-sans"),
        ["jetbrains-mono"] = new("'JetBrains Mono Variable', monospace", "--font-mono"),
        ["geist-mono"] = new("'Geist Mono Variable', monospace", "--font-mono"),
        ["noto-serif"] = new("'Noto Serif Variable', serif", "--font-serif"),
        ["roboto-slab"] = new("'Roboto Slab Variable', serif", "--font-serif"),
        ["merriweather"] = new("'Merriweather Variable', serif", "--font-serif"),
        ["lora"] = new("'Lora Variable', serif", "--font-serif"),
        ["playfair-display"] = new("'Playfair Display Variable', serif", "--font-serif"),
        ["eb-garamond"] = new("'EB Garamond Variable', serif", "--font-serif"),
        ["instrument-serif"] = new("'Instrument Serif', serif", "--font-serif")
    };

    public static string Generate(ShadcnThemeOptions options)
    {
        ThemeSelection selection = ResolveThemeSelection(options);
        Dictionary<string, string> light = BuildScheme(selection, dark: false);
        Dictionary<string, string> dark = BuildScheme(selection, dark: true);
        Dictionary<string, string> inline = BuildInlineTheme(options);

        ApplyRadius(light, options.Radius);
        AddSeriesAliases(light);
        AddSeriesAliases(dark);
        AddFonts(light, options);

        ApplyOverrides(light, options.LightOverrides);
        ApplyOverrides(dark, options.DarkOverrides);
        ApplyOverrides(inline, options.InlineOverrides);

        var builder = new StringBuilder(4096);
        builder.Append("/* Auto-generated by Soenneker.Quark.Gen.Tailwind from shadcn/ui themes at ");
        builder.Append(ShadcnThemeRegistry.SourceCommit);
        builder.AppendLine(". */");
        builder.AppendLine();

        AppendScheme(builder, ":root", light);
        builder.AppendLine();
        AppendScheme(builder, ".dark", dark);
        builder.AppendLine();
        AppendInlineTheme(builder, inline);

        return builder.ToString().TrimEnd();
    }

    private static ThemeSelection ResolveThemeSelection(ShadcnThemeOptions options)
    {
        string baseColor = NormalizeName(options.BaseColor, "neutral");

        if (!ShadcnThemeRegistry.IsBaseColorName(baseColor) || !ShadcnThemeRegistry.TryGetTheme(baseColor, out ShadcnTheme? baseTheme))
            throw new InvalidOperationException($"shadcn theme baseColor \"{options.BaseColor}\" is not supported by shadcn. Supported base colors: {Join(ShadcnThemeRegistry.BaseColorNames)}.");

        string themeName = NormalizeName(options.ThemeColor, baseColor);

        if (!ShadcnThemeRegistry.TryGetTheme(themeName, out ShadcnTheme? theme))
            throw new InvalidOperationException($"shadcn theme \"{options.ThemeColor}\" is not supported by shadcn. Supported themes: {Join(ShadcnThemeRegistry.ThemeNames)}.");

        if (!IsAvailableForBaseColor(baseColor, themeName))
            throw new InvalidOperationException($"shadcn theme \"{themeName}\" is not available for baseColor \"{baseColor}\" in shadcn.");

        string chartColor = NormalizeName(options.ChartColor, themeName);

        if (!ShadcnThemeRegistry.TryGetTheme(chartColor, out ShadcnTheme? chartTheme))
            throw new InvalidOperationException($"shadcn theme chartColor \"{options.ChartColor}\" is not supported by shadcn. Supported chart colors: {Join(ShadcnThemeRegistry.ThemeNames)}.");

        if (!IsAvailableForBaseColor(baseColor, chartColor))
            throw new InvalidOperationException($"Tailwind chartColor \"{chartColor}\" is not available for baseColor \"{baseColor}\" in shadcn.");

        return new ThemeSelection(baseColor, themeName, chartColor, baseTheme!, theme!, chartTheme!);
    }

    private static Dictionary<string, string> BuildScheme(ThemeSelection selection, bool dark)
    {
        IReadOnlyDictionary<string, string> baseValues = dark ? selection.BaseTheme.Dark : selection.BaseTheme.Light;
        IReadOnlyDictionary<string, string> themeValues = dark ? selection.Theme.Dark : selection.Theme.Light;
        IReadOnlyDictionary<string, string> chartValues = dark ? selection.ChartTheme.Dark : selection.ChartTheme.Light;

        var result = new Dictionary<string, string>(baseValues, StringComparer.OrdinalIgnoreCase);
        ApplyOverrides(result, themeValues);

        foreach (string name in _chartVariableNames)
        {
            if (chartValues.TryGetValue(name, out string? value))
                result[name] = value;
        }

        return result;
    }

    private static Dictionary<string, string> BuildInlineTheme(ShadcnThemeOptions options)
    {
        string headingValue = ResolveInlineHeadingFontValue(options);

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["font-sans"] = "var(--font-sans)",
            ["font-heading"] = headingValue,
            ["font-mono"] = "var(--font-mono)",
            ["color-background"] = "var(--background)",
            ["color-foreground"] = "var(--foreground)",
            ["color-card"] = "var(--card)",
            ["color-card-foreground"] = "var(--card-foreground)",
            ["color-popover"] = "var(--popover)",
            ["color-popover-foreground"] = "var(--popover-foreground)",
            ["color-primary"] = "var(--primary)",
            ["color-primary-foreground"] = "var(--primary-foreground)",
            ["color-secondary"] = "var(--secondary)",
            ["color-secondary-foreground"] = "var(--secondary-foreground)",
            ["color-muted"] = "var(--muted)",
            ["color-muted-foreground"] = "var(--muted-foreground)",
            ["color-accent"] = "var(--accent)",
            ["color-accent-foreground"] = "var(--accent-foreground)",
            ["color-destructive"] = "var(--destructive)",
            ["color-border"] = "var(--border)",
            ["color-input"] = "var(--input)",
            ["color-ring"] = "var(--ring)",
            ["color-chart-1"] = "var(--chart-1)",
            ["color-chart-2"] = "var(--chart-2)",
            ["color-chart-3"] = "var(--chart-3)",
            ["color-chart-4"] = "var(--chart-4)",
            ["color-chart-5"] = "var(--chart-5)",
            ["color-series-1"] = "var(--series-1)",
            ["color-series-2"] = "var(--series-2)",
            ["color-series-3"] = "var(--series-3)",
            ["color-series-4"] = "var(--series-4)",
            ["color-series-5"] = "var(--series-5)",
            ["color-sidebar"] = "var(--sidebar)",
            ["color-sidebar-foreground"] = "var(--sidebar-foreground)",
            ["color-sidebar-primary"] = "var(--sidebar-primary)",
            ["color-sidebar-primary-foreground"] = "var(--sidebar-primary-foreground)",
            ["color-sidebar-accent"] = "var(--sidebar-accent)",
            ["color-sidebar-accent-foreground"] = "var(--sidebar-accent-foreground)",
            ["color-sidebar-border"] = "var(--sidebar-border)",
            ["color-sidebar-ring"] = "var(--sidebar-ring)",
            ["radius-sm"] = "calc(var(--radius) * 0.6)",
            ["radius-md"] = "calc(var(--radius) * 0.8)",
            ["radius-lg"] = "var(--radius)",
            ["radius-xl"] = "calc(var(--radius) * 1.4)",
            ["radius-2xl"] = "calc(var(--radius) * 1.8)",
            ["radius-3xl"] = "calc(var(--radius) * 2.2)",
            ["radius-4xl"] = "calc(var(--radius) * 2.6)"
        };

        if (!string.IsNullOrWhiteSpace(options.SerifFont))
            values["font-serif"] = "var(--font-serif)";

        return values;
    }

    private static void ApplyRadius(IDictionary<string, string> values, string? radius)
    {
        if (string.IsNullOrWhiteSpace(radius))
            return;

        string normalized = NormalizeName(radius, string.Empty);

        switch (normalized)
        {
            case "":
            case "default":
                return;
            case "none":
                values["radius"] = "0";
                return;
            case "small":
            case "sm":
                values["radius"] = "0.45rem";
                return;
            case "medium":
            case "md":
                values["radius"] = "0.625rem";
                return;
            case "large":
            case "lg":
                values["radius"] = "0.875rem";
                return;
            default:
                values["radius"] = radius.Trim();
                return;
        }
    }

    private static void AddSeriesAliases(IDictionary<string, string> values)
    {
        for (var i = 1; i <= 5; i++)
            values[$"series-{i}"] = $"var(--chart-{i})";
    }

    private static void AddFonts(IDictionary<string, string> values, ShadcnThemeOptions options)
    {
        FontDefinition? bodyFont = TryGetFontDefinition(options.Font);
        if (bodyFont is not null)
        {
            values[TrimCssVariablePrefix(bodyFont.Variable)] = bodyFont.Family;
        }
        else if (!string.IsNullOrWhiteSpace(options.Font))
        {
            values["font-sans"] = BuildCustomFontStack(options.Font!, _defaultSansFallback);
        }

        if (!IsHeadingFontInherited(options))
            values["font-heading"] = BuildFontStack(options.HeadingFont!, _defaultSansFallback);

        if (!string.IsNullOrWhiteSpace(options.SerifFont))
            values["font-serif"] = BuildFontStack(options.SerifFont!, _defaultSerifFallback);

        if (!string.IsNullOrWhiteSpace(options.MonoFont))
            values["font-mono"] = BuildFontStack(options.MonoFont!, _defaultMonoFallback);
    }

    private static string ResolveInlineHeadingFontValue(ShadcnThemeOptions options)
    {
        if (!IsHeadingFontInherited(options))
            return "var(--font-heading)";

        FontDefinition? bodyFont = TryGetFontDefinition(options.Font);
        return bodyFont is null ? "var(--font-sans)" : $"var({bodyFont.Variable})";
    }

    private static bool IsHeadingFontInherited(ShadcnThemeOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.HeadingFont))
            return true;

        string heading = NormalizeName(options.HeadingFont, string.Empty);
        if (heading == "inherit")
            return true;

        string font = NormalizeName(options.Font, string.Empty);
        return !string.IsNullOrEmpty(font) && string.Equals(heading, font, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildFontStack(string value, string fallback)
    {
        FontDefinition? definition = TryGetFontDefinition(value);
        return definition is null ? BuildCustomFontStack(value, fallback) : definition.Family;
    }

    private static string BuildCustomFontStack(string value, string fallback)
    {
        string trimmed = value.Trim();

        if (trimmed.Contains(',', StringComparison.Ordinal) || trimmed.StartsWith("var(", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("calc(", StringComparison.OrdinalIgnoreCase) || trimmed.Contains('"', StringComparison.Ordinal) ||
            trimmed.Contains('\'', StringComparison.Ordinal))
        {
            return trimmed;
        }

        return CssString(trimmed) + ", " + fallback;
    }

    private static FontDefinition? TryGetFontDefinition(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        string normalized = NormalizeName(value, string.Empty);
        return _fontDefinitions.TryGetValue(normalized, out FontDefinition? definition) ? definition : null;
    }

    private static string TrimCssVariablePrefix(string value) => value.StartsWith("--", StringComparison.Ordinal) ? value.Substring(2) : value;

    private static void ApplyOverrides(IDictionary<string, string> target, IReadOnlyDictionary<string, string> overrides)
    {
        foreach ((string key, string value) in overrides)
        {
            if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
                target[ShadcnThemeOptions.NormalizeVariableName(key)] = value.Trim();
        }
    }

    private static void AppendScheme(StringBuilder builder, string selector, Dictionary<string, string> values)
    {
        builder.AppendLine(selector);
        builder.AppendLine("{");
        AppendVariables(builder, values, _schemeVariableOrder);
        builder.AppendLine("}");
    }

    private static void AppendInlineTheme(StringBuilder builder, Dictionary<string, string> values)
    {
        builder.AppendLine("@theme inline");
        builder.AppendLine("{");
        AppendVariables(builder, values, _inlineVariableOrder);
        builder.AppendLine("}");
    }

    private static void AppendVariables(StringBuilder builder, Dictionary<string, string> values, IReadOnlyCollection<string> orderedNames)
    {
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string name in orderedNames)
        {
            if (!values.TryGetValue(name, out string? value))
                continue;

            AppendVariable(builder, name, value);
            written.Add(name);
        }

        foreach (string name in values.Keys.Where(name => !written.Contains(name)).OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
            AppendVariable(builder, name, values[name]);
    }

    private static void AppendVariable(StringBuilder builder, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(value))
            return;

        builder.Append("  --");
        builder.Append(name.Trim());
        builder.Append(": ");
        builder.Append(value.Trim());
        builder.AppendLine(";");
    }

    private static bool IsAvailableForBaseColor(string baseColor, string themeName) =>
        string.Equals(baseColor, themeName, StringComparison.OrdinalIgnoreCase) || !ShadcnThemeRegistry.IsBaseColorName(themeName);

    private static string NormalizeName(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        return value.Trim().ToLowerInvariant().Replace('_', '-').Replace(' ', '-');
    }

    private static string Join(IEnumerable<string> values) => string.Join(", ", values);

    private static string CssString(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private sealed record ThemeSelection(string BaseColor, string ThemeName, string ChartColor, ShadcnTheme BaseTheme, ShadcnTheme Theme,
        ShadcnTheme ChartTheme);

    private sealed record FontDefinition(string Family, string Variable);
}
