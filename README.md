[![](https://img.shields.io/nuget/v/soenneker.quark.gen.tailwind.svg?style=for-the-badge)](https://www.nuget.org/packages/soenneker.quark.gen.tailwind/)
[![](https://img.shields.io/github/actions/workflow/status/soenneker/soenneker.quark.gen.tailwind/publish-package.yml?style=for-the-badge)](https://github.com/soenneker/soenneker.quark.gen.tailwind/actions/workflows/publish-package.yml)
[![](https://img.shields.io/github/actions/workflow/status/soenneker/soenneker.quark.gen.tailwind/build-and-test.yml?label=Build&style=for-the-badge)](https://github.com/soenneker/soenneker.quark.gen.tailwind/actions/workflows/build-and-test.yml)
[![](https://img.shields.io/nuget/dt/soenneker.quark.gen.tailwind.svg?style=for-the-badge)](https://www.nuget.org/packages/soenneker.quark.gen.tailwind/)
[![](https://img.shields.io/github/actions/workflow/status/soenneker/soenneker.quark.gen.tailwind/codeql.yml?label=CodeQL&style=for-the-badge)](https://github.com/soenneker/soenneker.quark.gen.tailwind/actions/workflows/codeql.yml)

# Soenneker.Quark.Gen.Tailwind

Build-time Tailwind CSS compilation for Quark applications, including Quark class manifests and shadcn-compatible theme variables.

## Install

```bash
dotnet add package Soenneker.Quark.Gen.Tailwind
```

Enable the build task in the application project:

```xml
<PropertyGroup>
  <TailwindGeneratorBuildEnabled>true</TailwindGeneratorBuildEnabled>
</PropertyGroup>
```

Node.js and npm must be available to the build. A successful build writes:

- `wwwroot/css/quark-tailwind.css`
- `wwwroot/css/quark-tailwind.min.css`

Load one of them from the application shell, usually the minified file:

```html
<link rel="stylesheet" href="css/quark-tailwind.min.css" />
```

The first enabled build creates a `tailwind` directory containing `input.css`, package metadata, manifests, and theme configuration. Commit the configuration files you customize; do not hand-edit the generated manifests or generated theme CSS.

## Theme configuration

Edit `tailwind/quark-shadcn.theme.json` to select the shadcn-compatible theme used by the generated CSS:

```json
{
  "baseColor": "Neutral",
  "theme": "Neutral",
  "chartColor": "Neutral",
  "radius": "Default"
}
```

You can also provide additional shadcn v4 design-system settings:

```json
{
  "style": "Mira",
  "baseColor": "Neutral",
  "theme": "Blue",
  "chartColor": "Emerald",
  "font": "Inter",
  "headingFont": "Inter"
}
```

Available style families are:

- `style`: `vega`, `nova`, `maia`, `lyra`, `mira`, `luma`, `sera`, `rhea`
- `baseColor`: `neutral`, `stone`, `zinc`, `mauve`, `olive`, `mist`, `taupe`
- `theme` and `chartColor`: any shadcn theme available for that base color
- `chartColor`: overrides only `chart-1` through `chart-5`

You can instead provide a shadcn preset code:

```json
{
  "preset": "b1D0dv72"
}
```

Override individual CSS variables when a preset needs exact values:

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

The same settings can be supplied through MSBuild properties, which take precedence over the JSON configuration:

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

## Output and manifest overrides

```xml
<PropertyGroup>
  <TailwindOutput>$(MSBuildProjectDirectory)\wwwroot\assets\quark.css</TailwindOutput>
  <TailwindManifestPath>$(MSBuildProjectDirectory)\tailwind\custom-manifest.txt</TailwindManifestPath>
</PropertyGroup>
```

`TailwindOutput` controls the full CSS path; the minified file is written beside it as `quark-tailwind.min.css`. `TailwindManifestPath` supplies an explicit upstream Quark manifest when it cannot be resolved from a project or package reference.
