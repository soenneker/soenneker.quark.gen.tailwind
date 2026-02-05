using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.Quark.Gen.Tailwind.BuildTasks.Abstract;

public interface ITailwindGeneratorWriteRunner
{
    ValueTask<int> Run(string[] args, CancellationToken cancellationToken);
}
