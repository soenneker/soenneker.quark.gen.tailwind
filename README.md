[![](https://img.shields.io/nuget/v/soenneker.quark.gen.tailwind.svg?style=for-the-badge)](https://www.nuget.org/packages/soenneker.quark.gen.tailwind/)
[![](https://img.shields.io/github/actions/workflow/status/soenneker/soenneker.quark.gen.tailwind/publish-package.yml?style=for-the-badge)](https://github.com/soenneker/soenneker.quark.gen.tailwind/actions/workflows/publish-package.yml)
[![](https://img.shields.io/nuget/dt/soenneker.quark.gen.tailwind.svg?style=for-the-badge)](https://www.nuget.org/packages/soenneker.quark.gen.tailwind/)
[![](https://img.shields.io/github/actions/workflow/status/soenneker/soenneker.quark.gen.tailwind/codeql.yml?label=CodeQL&style=for-the-badge)](https://github.com/soenneker/soenneker.quark.gen.tailwind/actions/workflows/codeql.yml)

# ![](https://user-images.githubusercontent.com/4441470/224455560-91ed3ee7-f510-4041-a8d2-3fc093025112.png) Soenneker.Quark.Gen.Tailwind
### Provides source generation for Quark and Tailwind CLI

## Installation

```
dotnet add package Soenneker.Quark.Gen.Tailwind
```

## Theme configuration

The Tailwind build task can generate `tailwind/quark-theme.generated.css` from shadcn v4 design-system settings. Add
`tailwind/quark-shadcn.theme.json` to the consuming app:

```json
{
  "style": "Mira",
  "baseColor": "Neutral",
  "theme": "Neutral",
  "chartColor": "Neutral",
  "font": "Inter",
  "headingFont": "Inter",
  "preset": "b1D0dv72"
}
```

The generator mirrors shadcn's current `baseColor + theme + chartColor` merge:

- `style`: `vega`, `nova`, `maia`, `lyra`, `mira`, `luma`, `sera`, `rhea`
- `baseColor`: `neutral`, `stone`, `zinc`, `mauve`, `olive`, `mist`, `taupe`
- `theme` and `chartColor`: any shadcn theme available for that base color
- `chartColor`: overrides only `chart-1` through `chart-5`

You can also provide just a shadcn preset code:

```json
{
  "preset": "b1D0dv72"
}
```

You can override individual CSS variables when a preset needs exact values:

```json
{
  "baseColor": "Zinc",
  "theme": "Blue",
  "chartColor": "Emerald",
  "light": {
    "primary": "oklch(0.623 0.214 259.815)"
  },
  "dark": {
    "primary": "oklch(0.809 0.105 251.813)"
  },
  "inline": {
    "font-heading": "var(--font-sans)"
  }
}
```

The same settings can be supplied through MSBuild properties:

```xml
<PropertyGroup>
  <ShadcnThemeStyle>Mira</ShadcnThemeStyle>
  <ShadcnThemeBaseColor>Neutral</ShadcnThemeBaseColor>
  <ShadcnTheme>Neutral</ShadcnTheme>
  <ShadcnThemeChartColor>Neutral</ShadcnThemeChartColor>
  <ShadcnThemeFont>Inter</ShadcnThemeFont>
  <ShadcnThemeHeadingFont>Inter</ShadcnThemeHeadingFont>
  <ShadcnThemePreset>b1D0dv72</ShadcnThemePreset>
</PropertyGroup>
```

If an external tool already generated the exact theme CSS, set `cssFilePath` in the JSON or `ShadcnThemeCssFile` in MSBuild to copy that file into the Tailwind build.
