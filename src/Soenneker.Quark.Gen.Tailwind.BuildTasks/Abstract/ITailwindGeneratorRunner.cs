using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.Quark.Gen.Tailwind.BuildTasks.Abstract;

/// <summary>
/// Defines the tailwind generator runner contract.
/// </summary>
public interface ITailwindGeneratorRunner
{
    /// <summary>
    /// Runs tailwind Generator Runner for the Tailwind Generator Runner.
    /// </summary>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>A task whose result is the requested value.</returns>
    ValueTask<int> Run(CancellationToken cancellationToken = default);
}
