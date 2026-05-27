using System;
using System.Collections.Generic;

namespace Soenneker.Quark.Gen.Tailwind.BuildTasks;

internal static class ShadcnPresetDecoder
{
    private const string _base62 = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    // Preset field order is part of shadcn's bit-packed preset format. Do not reorder.
    private static readonly string[] _presetStyles = ["nova", "vega", "maia", "lyra", "mira", "luma", "sera", "rhea"];

    private static readonly string[] _baseColors = ["neutral", "stone", "zinc", "gray", "mauve", "olive", "mist", "taupe"];

    private static readonly string[] _themes =
    [
        "neutral", "stone", "zinc", "gray", "amber", "blue", "cyan", "emerald", "fuchsia", "green", "indigo", "lime", "orange",
        "pink", "purple", "red", "rose", "sky", "teal", "violet", "yellow", "mauve", "olive", "mist", "taupe"
    ];

    private static readonly string[] _fonts =
    [
        "inter", "noto-sans", "nunito-sans", "figtree", "roboto", "raleway", "dm-sans", "public-sans", "outfit",
        "jetbrains-mono", "geist", "geist-mono", "lora", "merriweather", "playfair-display", "noto-serif", "roboto-slab",
        "oxanium", "manrope", "space-grotesk", "montserrat", "ibm-plex-sans", "source-sans-3", "instrument-sans", "eb-garamond",
        "instrument-serif"
    ];

    private static readonly string[] _fontHeadings =
    [
        "inherit", "inter", "noto-sans", "nunito-sans", "figtree", "roboto", "raleway", "dm-sans", "public-sans", "outfit",
        "jetbrains-mono", "geist", "geist-mono", "lora", "merriweather", "playfair-display", "noto-serif", "roboto-slab",
        "oxanium", "manrope", "space-grotesk", "montserrat", "ibm-plex-sans", "source-sans-3", "instrument-sans", "eb-garamond",
        "instrument-serif"
    ];

    private static readonly string[] _radii = ["default", "none", "small", "medium", "large"];
    private static readonly string[] _iconLibraries = ["lucide", "hugeicons", "tabler", "phosphor", "remixicon"];
    private static readonly string[] _menuAccents = ["subtle", "bold"];
    private static readonly string[] _menuColors = ["default", "inverted", "default-translucent", "inverted-translucent"];

    private static readonly PresetField[] _fieldsV1 =
    [
        new("menuColor", _menuColors, 3),
        new("menuAccent", _menuAccents, 3),
        new("radius", _radii, 4),
        new("font", _fonts, 6),
        new("iconLibrary", _iconLibraries, 6),
        new("theme", _themes, 6),
        new("baseColor", _baseColors, 6),
        new("style", _presetStyles, 6)
    ];

    private static readonly PresetField[] _fieldsV2 =
    [
        .._fieldsV1,
        new("chartColor", _themes, 6),
        new("fontHeading", _fontHeadings, 5)
    ];

    private static readonly Dictionary<string, string> _v1ChartColorMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["neutral"] = "blue",
        ["stone"] = "lime",
        ["zinc"] = "amber",
        ["mauve"] = "emerald",
        ["olive"] = "violet",
        ["mist"] = "rose",
        ["taupe"] = "cyan"
    };

    public static ShadcnPresetConfig? Decode(string code)
    {
        if (!LooksLikePresetCode(code))
            return null;

        code = code.Trim();
        char version = code[0];
        long bits = FromBase62(code.Substring(1));
        if (bits < 0)
            return null;

        PresetField[] fields = version == 'a' ? _fieldsV1 : _fieldsV2;
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var offset = 0;

        foreach (PresetField field in fields)
        {
            long index = (bits >> offset) & ((1L << field.Bits) - 1);
            values[field.Key] = index < field.Values.Length ? field.Values[index] : field.Values[0];
            offset += field.Bits;
        }

        if (!values.TryGetValue("fontHeading", out string? fontHeading))
            fontHeading = "inherit";

        string theme = values["theme"];
        string chartColor = values.TryGetValue("chartColor", out string? decodedChartColor)
            ? decodedChartColor
            : _v1ChartColorMap.GetValueOrDefault(theme, theme);

        return new ShadcnPresetConfig(
            values["style"],
            values["baseColor"],
            theme,
            chartColor,
            values["font"],
            fontHeading,
            values["radius"]);
    }

    private static bool LooksLikePresetCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        string trimmed = value.Trim();
        if (trimmed.Length < 2 || trimmed.Length > 10)
            return false;

        if (trimmed[0] is not ('a' or 'b'))
            return false;

        for (var i = 1; i < trimmed.Length; i++)
        {
            if (_base62.IndexOf(trimmed[i], StringComparison.Ordinal) < 0)
                return false;
        }

        return true;
    }

    private static long FromBase62(string value)
    {
        long result = 0;

        foreach (char current in value)
        {
            int index = _base62.IndexOf(current, StringComparison.Ordinal);
            if (index < 0)
                return -1;

            result = checked((result * 62) + index);
        }

        return result;
    }

    private readonly record struct PresetField(string Key, string[] Values, int Bits);
}

internal sealed record ShadcnPresetConfig(string Style, string BaseColor, string Theme, string ChartColor, string Font, string FontHeading, string Radius);
