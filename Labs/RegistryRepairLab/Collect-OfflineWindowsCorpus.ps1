[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ImagePath,

    [Parameter(Mandatory)]
    [ValidateRange(1, 100)]
    [int]$Index,

    [Parameter(Mandatory)]
    [string]$OutputRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Invoke-DismChecked {
    param([Parameter(Mandatory)][string[]]$Arguments)

    & "$env:SystemRoot\System32\dism.exe" @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "DISM failed with exit code $LASTEXITCODE."
    }
}

function Remove-VerifiedWorkDirectory {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$ExpectedParent
    )

    if (-not (Test-Path -LiteralPath $Path)) { return }
    $resolvedPath = (Resolve-Path -LiteralPath $Path).Path
    $resolvedParent = (Resolve-Path -LiteralPath $ExpectedParent).Path.TrimEnd('\') + '\'
    if (-not $resolvedPath.StartsWith($resolvedParent, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to delete a work directory outside the output root: $resolvedPath"
    }
    Remove-Item -LiteralPath $resolvedPath -Recurse -Force
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Administrator rights are required to mount a Windows image with DISM.'
}

$resolvedImage = (Resolve-Path -LiteralPath $ImagePath).Path
$extension = [IO.Path]::GetExtension($resolvedImage).ToLowerInvariant()
if ($extension -notin @('.wim', '.esd')) {
    throw 'Only official install.wim and install.esd images are accepted.'
}

$output = [IO.Path]::GetFullPath($OutputRoot)
[IO.Directory]::CreateDirectory($output) | Out-Null
$session = Join-Path $output ('.work-' + [Guid]::NewGuid().ToString('N'))
$mount = Join-Path $session 'mount'
$snapshot = Join-Path $output (
    'corpus-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + "-index$Index-" +
    [Guid]::NewGuid().ToString('N').Substring(0, 8))
$mounted = $false
$completed = $false

try {
    [IO.Directory]::CreateDirectory($session) | Out-Null
    [IO.Directory]::CreateDirectory($mount) | Out-Null
    [IO.Directory]::CreateDirectory($snapshot) | Out-Null

    $mountImage = $resolvedImage
    $mountIndex = $Index
    if ($extension -eq '.esd') {
        $mountImage = Join-Path $session 'exported.wim'
        Invoke-DismChecked @(
            '/Export-Image',
            "/SourceImageFile:$resolvedImage",
            "/SourceIndex:$Index",
            "/DestinationImageFile:$mountImage",
            '/Compress:max',
            '/CheckIntegrity'
        )
        $mountIndex = 1
    }

    Invoke-DismChecked @(
        '/Mount-Image',
        "/ImageFile:$mountImage",
        "/Index:$mountIndex",
        "/MountDir:$mount",
        '/ReadOnly',
        '/CheckIntegrity'
    )
    $mounted = $true

    $configSource = Join-Path $mount 'Windows\System32\config'
    $configTarget = Join-Path $snapshot 'Windows\System32\config'
    $userTarget = Join-Path $snapshot 'Users\Default'
    [IO.Directory]::CreateDirectory($configTarget) | Out-Null
    [IO.Directory]::CreateDirectory($userTarget) | Out-Null
    foreach ($name in @('SOFTWARE', 'SYSTEM', 'DEFAULT')) {
        Copy-Item -LiteralPath (Join-Path $configSource $name) -Destination (Join-Path $configTarget $name)
    }
    $defaultUser = Join-Path $mount 'Users\Default\NTUSER.DAT'
    if (Test-Path -LiteralPath $defaultUser) {
        Copy-Item -LiteralPath $defaultUser -Destination (Join-Path $userTarget 'NTUSER.DAT')
    }

    $project = Join-Path $PSScriptRoot 'RegistryRepair.CorpusTool\RegistryRepair.CorpusTool.csproj'
    & dotnet run --project $project -c Release -- `
        --image-root $snapshot `
        --output (Join-Path $snapshot 'manifest.json')
    if ($LASTEXITCODE -ne 0) {
        throw "Corpus validation failed with exit code $LASTEXITCODE."
    }
    $completed = $true
}
finally {
    $unmounted = -not $mounted
    $unmountError = $null
    if ($mounted) {
        try {
            Invoke-DismChecked @('/Unmount-Image', "/MountDir:$mount", '/Discard')
            $unmounted = $true
        }
        catch {
            $unmountError = $_
        }
    }
    if ($unmounted) {
        Remove-VerifiedWorkDirectory -Path $session -ExpectedParent $output
        if (-not $completed) {
            Remove-VerifiedWorkDirectory -Path $snapshot -ExpectedParent $output
        }
    }
    if ($null -ne $unmountError) {
        throw $unmountError
    }
}

Write-Output $snapshot
