using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace Soenneker.Quark.Gen.Tailwind.BuildTasks;

internal sealed class ProbingLoadContext : AssemblyLoadContext
{
    private readonly string _targetDir;
    private readonly string _frameworkDir;

    private static readonly string[] _sharedAssemblyPrefixes =
    [
        "Microsoft.Extensions.",
        "Microsoft.AspNetCore."
    ];

    public ProbingLoadContext(string targetDir)
    {
        _targetDir = targetDir;
        _frameworkDir = Path.Combine(targetDir, "wwwroot", "_framework");
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        string? name = assemblyName.Name;
        if (string.IsNullOrWhiteSpace(name))
            return null;

        if (IsSharedFrameworkAssembly(name))
        {
            try
            {
                return Default.LoadFromAssemblyName(assemblyName);
            }
            catch
            {
                // Fall back to project output probing.
            }
        }

        string candidate = Path.Combine(_targetDir, name + ".dll");
        if (File.Exists(candidate))
            return LoadFromAssemblyPath(candidate);

        candidate = Path.Combine(_frameworkDir, name + ".dll");
        if (File.Exists(candidate))
            return LoadFromAssemblyPath(candidate);

        return null;
    }

    private static bool IsSharedFrameworkAssembly(string name)
    {
        for (var i = 0; i < _sharedAssemblyPrefixes.Length; i++)
        {
            if (name.StartsWith(_sharedAssemblyPrefixes[i], StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
