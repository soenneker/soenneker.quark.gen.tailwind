using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.Quark.Gen.Tailwind.BuildTasks.Abstract;

/// <summary>
/// Defines the tailwind generator runner contract.
/// </summary>
public interface ITailwindGeneratorRunner
{
    /// <summary>
    /// Executes the run operation.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task containing the result of the operation.</returns>
    ValueTask<int> Run(CancellationToken cancellationToken = default);
}
