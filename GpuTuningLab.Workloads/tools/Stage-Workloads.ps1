param(
    [switch]$SyncToData
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$repoRoot = Split-Path -Parent $root
$artifactsRoot = Join-Path $root 'artifacts'
$stage = Join-Path $artifactsRoot 'workloads'
$publishTemp = Join-Path $artifactsRoot 'publish-temp'

& (Join-Path $PSScriptRoot 'Build-Workloads.ps1') -Configuration Release
if ($LASTEXITCODE -ne 0) { throw "Le build a echoue avec le code $LASTEXITCODE." }

foreach ($path in @($stage, $publishTemp)) {
    if (-not (Test-Path -LiteralPath $path)) { continue }
    $resolved = (Resolve-Path -LiteralPath $path).Path
    $resolvedRoot = (Resolve-Path -LiteralPath $root).Path
    if (-not $resolved.StartsWith($resolvedRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Chemin de staging hors des sources GPU : $resolved"
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}

$d3d11Stage = Join-Path $stage 'd3d11'
$dxrStage = Join-Path $stage 'dxr'
$dxrRuntime = Join-Path $dxrStage 'D3D12'
New-Item -ItemType Directory -Path $d3d11Stage, $dxrRuntime -Force | Out-Null

$managedProject = Join-Path $root 'D3D11\GpuTuningLab.Workload.csproj'
& dotnet publish $managedProject -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None -p:DebugSymbols=false -o $publishTemp
if ($LASTEXITCODE -ne 0) { throw "La publication D3D11 a echoue avec le code $LASTEXITCODE." }

Copy-Item -LiteralPath (Join-Path $publishTemp 'GpuTuningLab.Workload.exe') `
    -Destination (Join-Path $d3d11Stage 'GpuTuningLab.Workload.exe')
Copy-Item -LiteralPath (Join-Path $root 'D3D11\THIRD_PARTY_NOTICES.md') `
    -Destination (Join-Path $d3d11Stage 'THIRD_PARTY_NOTICES.md')

$dxrBuild = Join-Path $root 'RayTracing\bin\x64\Release'
Copy-Item -LiteralPath (Join-Path $dxrBuild 'GpuTuningLab.RayTracingWorkload.exe') `
    -Destination (Join-Path $dxrStage 'GpuTuningLab.RayTracingWorkload.exe')
Copy-Item -LiteralPath (Join-Path $dxrBuild 'D3D12\D3D12Core.dll') `
    -Destination (Join-Path $dxrRuntime 'D3D12Core.dll')
foreach ($name in @('THIRD_PARTY_NOTICES.md', 'D3D12_LICENSE.txt', 'D3D12_LICENSE-CODE.txt')) {
    Copy-Item -LiteralPath (Join-Path $root "RayTracing\$name") -Destination (Join-Path $dxrStage $name)
}

$manifest = Get-ChildItem -LiteralPath $stage -File -Recurse | Sort-Object FullName | ForEach-Object {
    [pscustomobject]@{
        path = $_.FullName.Substring($stage.Length + 1).Replace('\', '/')
        bytes = $_.Length
        sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }
}
$manifest | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $stage 'manifest.json') -Encoding UTF8

if (Get-ChildItem -LiteralPath $stage -File -Recurse | Where-Object Extension -eq '.pdb') {
    throw 'Le staging contient un PDB.'
}

Remove-Item -LiteralPath $publishTemp -Recurse -Force

if ($SyncToData) {
    $target = Join-Path $repoRoot 'data\tools\gpu-tuning'
    $resolvedRepo = (Resolve-Path -LiteralPath $repoRoot).Path
    if (Test-Path -LiteralPath $target) {
        $resolvedTarget = (Resolve-Path -LiteralPath $target).Path
        if (-not $resolvedTarget.StartsWith($resolvedRepo, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Cible hors du depot : $resolvedTarget"
        }
        Remove-Item -LiteralPath $resolvedTarget -Recurse -Force
    }
    Copy-Item -LiteralPath $stage -Destination $target -Recurse
    Write-Host "Package synchronise vers $target"
}

Get-ChildItem -LiteralPath $stage -File -Recurse | Select-Object FullName, Length

