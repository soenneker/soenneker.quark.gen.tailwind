using System.Collections.Generic;

namespace Soenneker.Quark.Gen.Tailwind.BuildTasks;

internal sealed class ShadcnThemeConfig
{
    public string? Css { get; set; }

    public string? RawCss { get; set; }

    public string? CssFilePath { get; set; }

    public string? CssFile { get; set; }

    public string? Style { get; set; }

    public string? BaseColor { get; set; }

    public string? Base { get; set; }

    public string? ThemeColor { get; set; }

    public string? Theme { get; set; }

    public string? ChartColor { get; set; }

    public string? Chart { get; set; }

    public string? Font { get; set; }

    public string? SansFont { get; set; }

    public string? SansSerifFont { get; set; }

    public string? HeadingFont { get; set; }

    public string? SerifFont { get; set; }

    public string? MonoFont { get; set; }

    public string? MonospaceFont { get; set; }

    public string? Radius { get; set; }

    public string? Preset { get; set; }

    public Dictionary<string, string>? Light { get; set; }

    public Dictionary<string, string>? Dark { get; set; }

    public Dictionary<string, string>? Inline { get; set; }

    public Dictionary<string, string>? ThemeVariables { get; set; }
}
