using Microsoft.CodeAnalysis;

namespace Soenneker.Quark.Gen.Tailwind;

/// <summary>
/// Source generator that runs only when the project is built (compilation).
/// Tailwind class collection and CLI compilation are handled by BuildTasks (RunTailwindGeneratorBuildTasks target).
/// </summary>
[Generator]
public sealed class TailwindGenerator : IIncrementalGenerator
{
    /// <summary>
    /// Initializes the analyzer entry point. Tailwind generation is performed by the package's MSBuild task.
    /// </summary>
    /// <param name="context">The incremental generator initialization context.</param>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Generator runs only on build; no incremental output. BuildTasks handle Blazor analysis and Tailwind CLI.
    }
}
