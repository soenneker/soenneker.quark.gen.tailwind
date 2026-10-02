namespace Soenneker.Quark.Gen.Tailwind.BuildTasks;

internal sealed record ThemeSelection(string BaseColor, string ThemeName, string ChartColor, ShadcnTheme BaseTheme, ShadcnTheme Theme,
    ShadcnTheme ChartTheme);
