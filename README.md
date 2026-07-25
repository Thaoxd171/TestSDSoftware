# SD Revit Test

Revit add-in submitted for the SD Software technical test. It adds a **SD Software** ribbon tab with two tools:

| Tool | What it does |
|---|---|
| **Bearing Plate** | Generates bearing plate detail drawings automatically — creates the detail views, dimensions and tags, then places them on sheets. |
| **Adjust Beam** | Trims and extends beams so every end keeps the clearance you set from the wall, pillar or beam it runs into. |

## Demo videos

| Tool | Video |
|---|---|
| Bearing Plate | _(link)_ |
| Adjust Beam | _(link)_ |

## Requirements

- Autodesk Revit **2024** or **2025**
- Visual Studio 2022 (or MSBuild 17+) with the .NET desktop development workload
- .NET Framework 4.8 developer pack (Revit 2024) and/or .NET 8 SDK (Revit 2025)

## Build

The Configuration name selects the Revit version:

```
msbuild SDRevitTest.sln /restore /p:Configuration=Revit_2024
msbuild SDRevitTest.sln /restore /p:Configuration=Revit_2025
```

| Configuration | Target framework | Revit |
|---|---|---|
| `Revit_2024` | `net48` | Revit 2024 |
| `Revit_2025` | `net8.0-windows` | Revit 2025 |

Output goes to `Revit\bin\<Configuration>\`.

> `/restore` must run with the same `Configuration` as the build — NuGet assets are stored per configuration
> (see `Directory.Build.props`).

## Install

The build writes a ready-to-use `SDRevitTest.addin` next to the output DLL. Copy it into

```
%APPDATA%\Autodesk\Revit\Addins\<version>\
```

and restart Revit. The `<Assembly>` path inside the manifest already points at the built DLL.

The build can do that copy for you:

```
msbuild SDRevitTest.sln /restore /p:Configuration=Revit_2025 /p:DeployAddin=true
```

It is off by default on purpose: once the manifest is deployed, Revit loads and locks the DLL at
start-up, so the project cannot be rebuilt until Revit is closed.

## Architecture

```
Revit/
├─ Core/          IExternalApplication, ribbon wiring, command base class, transaction helpers
├─ Mvvm/          ViewModelBase, RelayCommand, validation rules, shared WPF styles
├─ Extensions/    Extension methods over the Revit API (document, element, geometry, units, views)
├─ Features/
│   ├─ BearingPlate/   Cmd · Models · Services · ViewModels · Views
│   └─ AdjustBeam/     Cmd · Models · Services · ViewModels · Views
├─ Settings/      JSON persistence of the last used inputs
└─ Resources/     Ribbon icons
```

Each feature follows the same layering:

- **Models** — plain data, no Revit API where avoidable.
- **Services** — all Revit API work. Geometry maths is kept in separate, Revit-free classes so it can be
  reasoned about (and tested) on its own.
- **ViewModels** — MVVM, `INotifyPropertyChanged` + `RelayCommand`, no Revit API calls.
- **Views** — WPF windows, bound to the view model only.

Version differences between Revit 2024 and 2025 (for example `ElementId.IntegerValue` vs `ElementId.Value`)
are isolated behind extension methods guarded with `#if Revit_2024` / `#if Revit_2025`.

## Notes

- All user-facing strings are in English.
- Every command runs inside a single Revit transaction and rolls back on failure.
- Cancelling a dialog returns `Result.Cancelled`, so Revit does not report an error.
