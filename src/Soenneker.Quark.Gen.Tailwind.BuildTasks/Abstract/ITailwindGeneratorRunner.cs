using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.Quark.Gen.Tailwind.BuildTasks.Abstract;

public interface ITailwindGeneratorRunner
{
    ValueTask<int> Run(CancellationToken cancellationToken = default);
}
