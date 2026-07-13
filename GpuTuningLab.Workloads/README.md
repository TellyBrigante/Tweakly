# GPU tuning workloads

This directory contains the complete source required to rebuild the two GPU
workloads distributed with Tweakly. Generated binaries are not source files;
they are staged under `artifacts/` and copied to `data/tools/gpu-tuning/` only
when `Stage-Workloads.ps1 -SyncToData` is used.

Requirements:

- .NET 8 SDK
- Visual Studio Build Tools with Desktop development with C++
- Windows 10/11 SDK

Build both workloads:

```powershell
.\tools\Build-Workloads.ps1 -Configuration Release
```

Rebuild, hash and synchronize the package used by Tweakly:

```powershell
.\tools\Stage-Workloads.ps1 -SyncToData
```

