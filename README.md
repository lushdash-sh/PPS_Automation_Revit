# PPS_Automation_Revit

Revit add-in for automating dimensions on precast concrete assemblies.

## Prerequisites

- Windows and Git.
- Autodesk Revit 2027 installed on the development machine. This project
  references the Revit API assemblies installed with Revit; the add-in must be
  built against the Revit version it will run in.
- Install the latest Windows x64 **.NET SDK** from
  [dotnet.microsoft.com/download](https://dotnet.microsoft.com/download).
  Also install the **.NET Framework 4.8 Developer Pack** (targeting pack) from
  [Microsoft's .NET Framework downloads](https://dotnet.microsoft.com/en-us/download/dotnet-framework/net48).
  Installing only the .NET runtime is not sufficient to build this project.
  Alternatively, Visual Studio users can select the **.NET desktop development**
  workload and **.NET Framework 4.8 targeting pack** in Visual Studio Installer.
- The project currently expects the Revit API at
  `C:\Program Files\Autodesk\Revit 2027\RevitAPI.dll` and
  `C:\Program Files\Autodesk\Revit 2027\RevitAPIUI.dll`.

If Revit is installed elsewhere, or you are targeting a different Revit year,
edit the `RevitAPI` and `RevitAPIUI` `HintPath` values in
`StructAutoDetailing.csproj` to point to that installation's matching API DLLs
before building.

## Clone, restore, and build

Install Git, Revit 2027, the .NET SDK, and the .NET Framework 4.8 Developer
Pack first. Then open PowerShell and run:

```powershell
git clone https://github.com/lushdash-sh/PPS_Automation_Revit.git
cd PPS_Automation_Revit
dotnet --info
dotnet restore .\StructAutoDetailing.csproj
dotnet build .\StructAutoDetailing.csproj --configuration Release
```

`dotnet restore` restores NuGet dependencies declared by the project. It does
not install the .NET SDK, .NET Framework targeting pack, Revit, or Revit API
assemblies. The current project has no third-party `PackageReference` entries;
the Revit API assemblies are supplied by the local Revit installation and are
referenced through the project file.

If restore or build reports that the .NET Framework 4.8 reference assemblies
are missing, install the .NET Framework 4.8 Developer Pack and rerun the build.
If it reports a missing Revit API DLL, correct the API `HintPath` values as
described above.

The output DLL is:

```text
bin\Release\net48\StructAutoDetailing.dll
```

## Installation and deployment

1. Ensure the target machine has the matching Revit version installed.
2. Copy `bin\Release\net48\StructAutoDetailing.dll` to a permanent location on
   that machine. For example, the following PowerShell commands install it
   under the current user's Revit 2027 Addins folder:

   ```powershell
   $revitYear = "2027"
   $addinDir = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$revitYear"
   New-Item -ItemType Directory -Force -Path $addinDir
   Copy-Item .\bin\Release\net48\StructAutoDetailing.dll $addinDir
   ```

3. Create `StructAutoDetailing.addin` in `$addinDir`. In the manifest below,
   replace `YOUR_4_CHARACTER_VENDOR_ID` with your own four-character vendor
   identifier, and `YOUR_NAME_OR_ORGANIZATION` with your own name or
   organization. Set `<Assembly>` to the full local path of the DLL copied in
   step 2. Keep `<FullClassName>` as shown; it identifies the add-in entry
   point in this project.

   ```xml
   <?xml version="1.0" encoding="utf-8"?>
   <RevitAddIns>
     <AddIn Type="Application">
       <Name>StructAuto Detailing UI</Name>
       <Assembly>C:\Users\YOUR_USER\AppData\Roaming\Autodesk\Revit\Addins\2027\StructAutoDetailing.dll</Assembly>
       <AddInId>b84a9e25-15a0-410a-814a-71eaec8b6c45</AddInId>
       <FullClassName>StructAutoDetailing.App</FullClassName>
       <VendorId>YOUR_4_CHARACTER_VENDOR_ID</VendorId>
       <VendorDescription>YOUR_NAME_OR_ORGANIZATION</VendorDescription>
     </AddIn>
   </RevitAddIns>
   ```

   The example `<Assembly>` path is a placeholder: replace it with the actual
   absolute path for the Windows user and Revit year on the target machine. Keep
   the same `<AddInId>` for subsequent updates to this add-in; use a new GUID
   only when creating a separate add-in identity.

4. Save the manifest as
   `%appdata%\Autodesk\Revit\Addins\[Year]\StructAutoDetailing.addin` (use the
   actual Revit year, such as `2027`).
5. Restart Revit. The add-in should appear on the **PPS Shop Tools** ribbon tab.

The DLL does not need to be rebuilt for a user's Windows profile path. The
manifest's `<Assembly>` element must point to the DLL's actual path on each
target machine. The build does, however, use local Revit API paths from the
project file, which must exist on the build machine.

## How to operate

1. Open an **Elevation**, **Section**, or **Detail** view of a precast assembly.
2. Ensure the view is clear of previous manual dimensioning. The beam command
   can clean up its own previous runs.
3. Open the **PPS Shop Tools** ribbon tab.
4. Click **Smart Column Dimensioning**, **Smart Wall Dimensioning** or **Smart Beam Dimensioning**.
5. When prompted, select the main precast assembly in the view.
6. The command processes the assembly geometry and places dimension tiers
   according to the implemented detailing rules.

## Current progress and scope

- **Columns and walls:** In progress. The current effort uses reference planes as a source of detecting the dimensions. We plan to implement geometry
  to detect concrete dimensions and nested embeds, including corbels, lifting
  hooks, and dowels, for automated dimension tiers.
- **Beams:** Elevation-view dimensioning is also in progress.
- **Future scope:** Expand and stabilize the logic for **Section** views and
  **Plan detail** views after elevation workflows are complete.
