using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Soenneker.Blazor.Utils.ComponentHtmlRenderers;
using Soenneker.Extensions.Task;
using Soenneker.Utils.File.Abstract;

namespace Soenneker.Quark.Gen.Tailwind.BuildTasks;

/// <summary>
/// Parses .razor files for Quark component usages, resolves expressions via reflection, and renders each to HTML to extract Tailwind elements.
/// </summary>
internal static class QuarkComponentUsageCollector
{
    /// <summary>Max time to wait for a single component render; prevents one hanging component from blocking the build.</summary>
    private static readonly TimeSpan PerComponentRenderTimeout = TimeSpan.FromSeconds(5);

    private static readonly Regex ComponentTagRegex = new(
        @"<([A-Z][a-zA-Z0-9]*)\s*([^>]*(?:/[>]|>))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex AttributeRegex = new(
        @"(?:^|\s)([A-Za-z][A-Za-z0-9]*)\s*=\s*[""']([^""']*)[""']",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ExpressionRegex = new(
        @"^[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Parses .razor and .cshtml files in projectDir for Quark component usages, renders each unique (Type, params) with the renderer, and extracts elements.
    /// </summary>
    public static async Task<HashSet<string>> Collect(
        Assembly appAssembly,
        string projectDir,
        ComponentHtmlRenderer renderer,
        IReadOnlyDictionary<string, Type> tagToType,
        IFileUtil fileUtil,
        CancellationToken cancellationToken = default)
    {
        List<(string TagName, Dictionary<string, string> Attrs)> usages = await ParseRazorFilesForQuarkUsages(projectDir, tagToType.Keys, fileUtil, cancellationToken).NoSync();
        if (usages.Count == 0)
            return [];

        List<Assembly> assembliesToSearch = GetAssembliesToSearch(appAssembly);
        var uniqueElements = new HashSet<string>(StringComparer.Ordinal);

        foreach ((string tagName, Dictionary<string, string> attrs) in usages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!tagToType.TryGetValue(tagName, out Type? componentType))
                continue;

            var parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach ((string paramName, string expr) in attrs)
            {
                if (paramName == "__empty__" || string.IsNullOrEmpty(expr))
                    continue;
                object? value = ResolveExpression(expr, assembliesToSearch);
                if (value != null)
                {
                    object? paramValue = WrapBuilderInCssValueIfNeeded(value, componentType, paramName);
                    parameters[paramName] = paramValue ?? value;
                }
            }

            try
            {
                string? html = await RenderWithTimeout(
                    () => renderer.RenderToHtml(componentType, parameters),
                    PerComponentRenderTimeout,
                    $"Skipped {tagName} with params",
                    cancellationToken).NoSync();
                if (!string.IsNullOrEmpty(html))
                {
                    foreach (string element in ExtractUniqueElementsFromHtml(html))
                        uniqueElements.Add(element);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Skipped {tagName} with params: {ex.Message}");
            }
        }

        return uniqueElements;
    }

    /// <summary>Runs a render task with a timeout so one hanging component does not block the build.</summary>
    private static async Task<string?> RenderWithTimeout(
        Func<Task<string?>> render,
        TimeSpan timeout,
        string nameForLog,
        CancellationToken cancellationToken)
    {
        Task<string?> renderTask = render();
        Task delayTask = Task.Delay(timeout, cancellationToken);
        var completed = await Task.WhenAny(renderTask, delayTask).NoSync();
        if (completed == renderTask)
            return await renderTask.NoSync();
        Console.WriteLine($"{nameForLog}: render timed out after {(int)timeout.TotalSeconds}s.");
        return null;
    }

    /// <summary>
    /// Parses .razor and .cshtml files for PascalCase component tags with resolvable attribute expressions.
    /// Returns list of (TagName, Dictionary of ParamName -> Expression).
    /// </summary>
    private static async Task<List<(string TagName, Dictionary<string, string> Attrs)>> ParseRazorFilesForQuarkUsages(
        string projectDir,
        IEnumerable<string> knownTags,
        IFileUtil fileUtil,
        CancellationToken cancellationToken)
    {
        var results = new List<(string TagName, Dictionary<string, string> Attrs)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        if (!Directory.Exists(projectDir))
            return results;

        foreach (string path in Directory.EnumerateFiles(projectDir, "*.razor", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(projectDir, "*.cshtml", SearchOption.AllDirectories)))
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                string content = await fileUtil.Read(path, log: false, cancellationToken).NoSync();
                foreach (Match tagMatch in ComponentTagRegex.Matches(content))
                {
                    string tagName = tagMatch.Groups[1].Value;
                    if (!knownTags.Contains(tagName, StringComparer.Ordinal))
                        continue;

                    string attrsSection = tagMatch.Groups[2].Value;
                    if (attrsSection.StartsWith("/", StringComparison.Ordinal) || attrsSection.StartsWith(">", StringComparison.Ordinal))
                        continue;

                    var attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (Match attrMatch in AttributeRegex.Matches(attrsSection))
                    {
                        string paramName = attrMatch.Groups[1].Value;
                        string value = attrMatch.Groups[2].Value.Trim();

                        if (paramName.StartsWith("@", StringComparison.Ordinal) || value.StartsWith("@", StringComparison.Ordinal))
                            continue;
                        if (paramName is "ChildContent" or "ref" or "key")
                            continue;
                        if (!ExpressionRegex.IsMatch(value))
                            continue;

                        attrs[paramName] = value;
                    }

                    if (attrs.Count == 0)
                        attrs["__empty__"] = "";

                    string key = MakeKey(tagName, attrs);
                    if (seen.Add(key))
                        results.Add((tagName, attrs));
                }
            }
            catch
            {
                // Skip unreadable files
            }
        }

        return results;
    }

    private static string MakeKey(string tagName, Dictionary<string, string> attrs)
    {
        var parts = new List<string> { tagName };
        foreach (KeyValuePair<string, string> kv in attrs.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            if (kv.Key != "__empty__")
                parts.Add($"{kv.Key}={kv.Value}");
        }
        return string.Join("|", parts);
    }

    /// <summary>
    /// Resolves an expression like "Margin.Is3.FromEnd" via reflection across the given assemblies.
    /// </summary>
    private static object? ResolveExpression(string expression, IEnumerable<Assembly> assemblies)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return null;

        string[] parts = expression.Split('.');
        if (parts.Length < 2)
            return null;

        Type? type = null;
        foreach (Assembly asm in assemblies)
        {
            type = asm.GetType($"Soenneker.Quark.{parts[0]}");
            if (type != null)
                break;
        }

        if (type == null)
            return null;

        object? current = null;
        for (var i = 1; i < parts.Length; i++)
        {
            string part = parts[i];
            if (current == null)
            {
                MemberInfo[] members = type.GetMember(part, MemberTypes.Property | MemberTypes.Field, BindingFlags.Public | BindingFlags.Static);
                if (members.Length == 0)
                    return null;

                if (members[0] is PropertyInfo pi)
                    current = pi.GetValue(null);
                else if (members[0] is FieldInfo fi)
                    current = fi.GetValue(null);
                else
                    return null;
            }
            else
            {
                Type t = current.GetType();
                MemberInfo[] members = t.GetMember(part, MemberTypes.Property | MemberTypes.Field, BindingFlags.Public | BindingFlags.Instance);
                if (members.Length == 0)
                    return null;

                if (members[0] is PropertyInfo pi)
                    current = pi.GetValue(current);
                else if (members[0] is FieldInfo fi)
                    current = fi.GetValue(current);
                else
                    return null;
            }

            if (current == null)
                return null;
        }

        return current;
    }

    /// <summary>
    /// Wraps a builder (e.g. MarginBuilder) in CssValue{T} when the component parameter expects CssValue{T}?.
    /// </summary>
    private static object? WrapBuilderInCssValueIfNeeded(object value, Type componentType, string paramName)
    {
        PropertyInfo? prop = componentType.GetProperty(paramName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (prop == null)
            return value;

        Type targetType = prop.PropertyType;
        if (targetType.IsGenericType && targetType.GetGenericTypeDefinition() == typeof(Nullable<>))
            targetType = targetType.GetGenericArguments()[0];

        if (!targetType.IsGenericType || targetType.GetGenericTypeDefinition() != typeof(CssValue<>))
            return value;

        Type builderType = targetType.GetGenericArguments()[0];
        if (!builderType.IsInstanceOfType(value))
            return value;

        MethodInfo? opImplicit = targetType.GetMethod("op_Implicit", BindingFlags.Public | BindingFlags.Static, null, [builderType], null);
        if (opImplicit != null)
            return opImplicit.Invoke(null, [value]);

        return value;
    }

    private static List<Assembly> GetAssembliesToSearch(Assembly appAssembly)
    {
        var list = new List<Assembly> { appAssembly };
        foreach (AssemblyName refName in appAssembly.GetReferencedAssemblies())
        {
            try
            {
                Assembly asm = Assembly.Load(refName);
                if (asm.GetName().Name?.StartsWith("Soenneker.Quark", StringComparison.OrdinalIgnoreCase) == true)
                    list.Add(asm);
            }
            catch
            {
                // ignore
            }
        }

        Assembly? quarkSuite = typeof(Component).Assembly;
        if (quarkSuite != null && !list.Contains(quarkSuite))
            list.Insert(0, quarkSuite);

        return list;
    }

    private static readonly Regex ElementWithClassRegex = new(
        @"<(\w+)[^>]*\bclass\s*=\s*[""']([^""']*)[""'][^>]*>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static IEnumerable<string> ExtractUniqueElementsFromHtml(string html)
    {
        foreach (Match m in ElementWithClassRegex.Matches(html))
        {
            string tag = m.Groups[1].Value;
            string classValue = m.Groups[2].Value;
            if (string.IsNullOrWhiteSpace(classValue))
                continue;

            string normalizedClasses = string.Join(" ", classValue
                .Split((char[]?)[' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .OrderBy(s => s, StringComparer.Ordinal));
            if (normalizedClasses.Length == 0)
                continue;

            yield return $"<{tag} class=\"{normalizedClasses}\"></{tag}>";
        }
    }

    /// <summary>
    /// Builds a tag name -> Quark component Type map from the Quark assembly.
    /// </summary>
    public static Dictionary<string, Type> BuildTagToTypeMap(Assembly appAssembly)
    {
        Assembly? quarkAssembly = typeof(Component).Assembly;
        if (quarkAssembly == null)
            return new Dictionary<string, Type>(StringComparer.Ordinal);

        Type componentBase = typeof(Component);
        var map = new Dictionary<string, Type>(StringComparer.Ordinal);

        foreach (Type type in quarkAssembly.GetExportedTypes())
        {
            if (type.IsAbstract || type.IsInterface)
                continue;
            if (!componentBase.IsAssignableFrom(type))
                continue;
            if (type.Name.Contains('`'))
                continue;

            map[type.Name] = type;
        }

        return map;
    }
}
