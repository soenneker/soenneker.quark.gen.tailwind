using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.Quark.Gen.Tailwind.BuildTasks.Abstract;

/// <summary>
/// Generates Tailwind CSS outputs for a consuming Quark project.
/// </summary>
public interface ITailwindGeneratorRunner
{
    /// <summary>
    /// Generates the configured full and minified Tailwind CSS outputs.
    /// </summary>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>The process exit code: zero on success; otherwise nonzero.</returns>
    ValueTask<int> Run(CancellationToken cancellationToken = default);
}
