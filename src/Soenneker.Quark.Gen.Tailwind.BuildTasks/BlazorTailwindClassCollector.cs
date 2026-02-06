using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace Soenneker.Quark.Gen.Tailwind.BuildTasks;

/// <summary>
/// Scans Blazor (.razor) files and derives Tailwind CSS classes from Quark component usage
/// (e.g. ColumnSize="ColumnSize.Is10.OnMd" → md:col-span-10).
/// </summary>
public static class BlazorTailwindClassCollector
{
    // Match ColumnSize/Size/SizeMedium/SizeLarge="ColumnSize.IsN.OnBp" or @"...". Capture size (12|11|10|...|1) before single digits.
    private static readonly Regex ColumnSizeLiteralRegex = new(
        @"(?:ColumnSize|Size|SizeMedium|SizeLarge)\s*=\s*[""'](?:\s*@?\s*)?ColumnSize\.(Is(12|11|10|9|8|7|6|5|4|3|2|1)|Auto)(\.On(Sm|Md|Lg|Xl|Xxl))?\s*[""']",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Collects all Tailwind classes that can be inferred from Blazor markup under <paramref name="projectDir"/>.
    /// </summary>
    /// <param name="projectDir">Project directory to scan (e.g. containing Pages, Components, Layout).</param>
    /// <param name="includeSubdirs">Whether to scan subdirectories (default true).</param>
    /// <returns>Set of Tailwind class names (e.g. "md:col-span-10", "col-span-12").</returns>
    public static HashSet<string> CollectTailwindClassesFromBlazor(string projectDir, bool includeSubdirs = true)
    {
        var classes = new HashSet<string>();
        if (string.IsNullOrWhiteSpace(projectDir) || !Directory.Exists(projectDir))
            return classes;

        var searchOption = includeSubdirs ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        foreach (var path in Directory.EnumerateFiles(projectDir, "*.razor", searchOption))
        {
            try
            {
                var text = File.ReadAllText(path);
                CollectFromContent(text, classes);
            }
            catch
            {
                // Skip files we can't read
            }
        }

        // Include base classes used by Column when no size is set
        classes.Add("q-col");
        classes.Add("min-w-0");
        classes.Add("flex-1");

        return classes;
    }

    /// <summary>
    /// Parses markup content and adds inferred Tailwind classes to <paramref name="classes"/>.
    /// </summary>
    public static void CollectFromContent(string markupContent, HashSet<string> classes)
    {
        if (string.IsNullOrEmpty(markupContent))
            return;

        foreach (Match m in ColumnSizeLiteralRegex.Matches(markupContent))
        {
            var sizePart = m.Groups[2].Value; // "1".."12" or empty for Auto
            var size = sizePart.Length > 0 ? sizePart : "auto";
            var breakpoint = m.Groups[4].Success ? m.Groups[4].Value : null; // Sm, Md, Lg, Xl, Xxl

            var tailwindClass = ColumnSizeToTailwindClass(size, breakpoint);
            if (!string.IsNullOrEmpty(tailwindClass))
                classes.Add(tailwindClass);
        }
    }

    /// <summary>
    /// Maps a ColumnSize literal (e.g. "10", "auto") and optional breakpoint (e.g. "Md") to a Tailwind class.
    /// </summary>
    public static string ColumnSizeToTailwindClass(string size, string? breakpoint)
    {
        var baseClass = size switch
        {
            "1" => "col-span-1",
            "2" => "col-span-2",
            "3" => "col-span-3",
            "4" => "col-span-4",
            "5" => "col-span-5",
            "6" => "col-span-6",
            "7" => "col-span-7",
            "8" => "col-span-8",
            "9" => "col-span-9",
            "10" => "col-span-10",
            "11" => "col-span-11",
            "12" => "col-span-12",
            "auto" => "col-auto",
            _ => ""
        };

        if (string.IsNullOrEmpty(baseClass))
            return "";

        var bpToken = BreakpointToTailwindToken(breakpoint);
        if (string.IsNullOrEmpty(bpToken))
            return baseClass;

        return $"{bpToken}:{baseClass}";
    }

    private static string? BreakpointToTailwindToken(string? breakpoint)
    {
        if (string.IsNullOrEmpty(breakpoint))
            return null;
        return breakpoint.ToLowerInvariant() switch
        {
            "sm" => "sm",
            "md" => "md",
            "lg" => "lg",
            "xl" => "xl",
            "xxl" => "2xl",
            _ => null
        };
    }
}
