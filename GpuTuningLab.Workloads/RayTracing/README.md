# Charge DirectX Raytracing

Cette charge est derivee de `D3D12RaytracingSimpleLighting`, publie par Microsoft
dans DirectX Graphics Samples sous licence MIT.

Modifications du prototype :

- adaptateur NVIDIA obligatoire ;
- rendu DXR masque en `1 920 x 1 080 px` ;
- echauffement separe de la mesure ;
- duree utile de `2 s` a `120 s` ;
- synchronisation GPU avant le calcul final ;
- score en millions de rayons primaires par seconde ;
- sortie standard lisible par `GpuTuningLab.Cli` ;
- aucun reglage GPU applique.

Construction :

```powershell
.\tools\Build-All.ps1 -Configuration Release
```

Execution directe :

```powershell
.\GpuTuningLab.RayTracingWorkload\bin\x64\Release\GpuTuningLab.RayTracingWorkload.exe --seconds 30 --warmup 2
```

La CLI doit rester le point d'entree normal, car elle bloque le lancement si un
autre processus utilise plus de `5 %` du calcul ou de la memoire GPU.
