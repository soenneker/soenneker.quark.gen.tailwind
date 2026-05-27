using System;
using System.Collections.Generic;

namespace Soenneker.Quark.Gen.Tailwind.BuildTasks;

// Data copied from shadcn-ui/ui apps/v4/registry/styles.tsx and packages/shadcn/src/preset/preset.ts.
internal static class ShadcnStyleRegistry
{
    private static readonly string[] _names =
    [
        "vega",
        "nova",
        "maia",
        "lyra",
        "mira",
        "luma",
        "sera",
        "rhea"
    ];

    private static readonly HashSet<string> _nameSet = new(_names, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> Names => _names;

    public static string[] NamesArray => _names;

    public static bool IsSupported(string name) => _nameSet.Contains(name);
}
