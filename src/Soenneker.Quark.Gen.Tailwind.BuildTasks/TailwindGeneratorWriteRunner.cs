using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.Quark.Gen.Tailwind.BuildTasks;

///<inheritdoc cref="Abstract.ITailwindGeneratorWriteRunner"/>
public sealed class TailwindGeneratorWriteRunner : Abstract.ITailwindGeneratorWriteRunner
{
    public ValueTask<int> Run(string[] args, CancellationToken cancellationToken)
    {
        var map = ParseArgs(args);
        if (!map.TryGetValue("--targetPath", out var targetPath) || string.IsNullOrWhiteSpace(targetPath))
            return ValueTask.FromResult(Fail("Missing required --targetPath"));
        if (!map.TryGetValue("--projectDir", out var projectDir) || string.IsNullOrWhiteSpace(projectDir))
            return ValueTask.FromResult(Fail("Missing required --projectDir"));
        targetPath = Path.GetFullPath(targetPath.Trim().Trim('"'));
        projectDir = Path.GetFullPath(projectDir.Trim().Trim('"'));
        if (!File.Exists(targetPath))
            return ValueTask.FromResult(Fail($"Target assembly not found: {targetPath}"));
        // Skeleton: add your build-time logic here (e.g. generate files under projectDir).
        return ValueTask.FromResult(0);
    }

    private static Dictionary<string, string> ParseArgs(string[] args)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal) && i + 1 < args.Length)
            {
                map[args[i]] = args[i + 1];
                i++;
            }
        }
        return map;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }
}
