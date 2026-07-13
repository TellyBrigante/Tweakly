param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$managedProject = Join-Path $root 'D3D11\GpuTuningLab.Workload.csproj'
$nativeProject = Join-Path $root 'RayTracing\GpuTuningLab.RayTracingWorkload.vcxproj'

$msBuildCandidates = @(
    'C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\MSBuild.exe',
    'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe',
    'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe'
)
$msBuild = $msBuildCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $msBuild) {
    throw 'MSBuild C++ introuvable. Installe Visual Studio Build Tools avec Desktop development with C++.'
}

& dotnet build $managedProject -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "Le build D3D11 a echoue avec le code $LASTEXITCODE." }

& $msBuild $nativeProject /restore /m /t:Rebuild "/p:Configuration=$Configuration" /p:Platform=x64 /v:minimal
if ($LASTEXITCODE -ne 0) { throw "Le build ray tracing a echoue avec le code $LASTEXITCODE." }

Write-Host "Workloads GPU $Configuration : build termine."

