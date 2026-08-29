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
    /// Initializes the Tailwind Generator so it is ready for use.
    /// </summary>
    /// <param name="context">HTTP context containing the Authorization header.</param>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Generator runs only on build; no incremental output. BuildTasks handle Blazor analysis and Tailwind CLI.
    }
}
