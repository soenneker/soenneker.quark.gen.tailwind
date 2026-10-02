using System.Collections.Generic;

namespace Soenneker.Quark.Gen.Tailwind.BuildTasks;

internal sealed class ShadcnTheme
{
    public ShadcnTheme(IReadOnlyDictionary<string, string> light, IReadOnlyDictionary<string, string> dark)
    {
        Light = light;
        Dark = dark;
    }

    public IReadOnlyDictionary<string, string> Light { get; }

    public IReadOnlyDictionary<string, string> Dark { get; }
}
