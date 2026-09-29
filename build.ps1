param(
    [string]$GameDir = 'D:\Secondary Steam\steamapps\common\Mount & Blade II Bannerlord',
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Join-Path $repo 'BetterBannerlordNavmeshing.csproj'
$moduleDir = Join-Path $GameDir 'Modules\BetterBannerlordNavmeshing'
$variants = @('Win64_Shipping_wEditor', 'Win64_Shipping_Client')

Write-Host "BetterBannerlordNavmeshing build"
Write-Host "  repo:   $repo"
Write-Host "  game:   $GameDir"
Write-Host "  module: $moduleDir"
Write-Host ""

foreach ($variant in $variants) {
    Write-Host "== Building $variant ($Configuration) =="
    & dotnet build $proj -c $Configuration -p:GameDir="$GameDir" -p:GameBinFolder=$variant
    if ($LASTEXITCODE -ne 0) {
        throw "Build failed for $variant (exit code $LASTEXITCODE)"
    }

    # Deploy the PDB beside the DLL so crash dumps resolve our frames to source lines.
    # (The csproj's DeployToModule target only copies the DLL.) PDBs are never zipped/released.
    $builtPdb = Join-Path $repo "bin\$Configuration\net472\BetterBannerlordNavmeshing.pdb"
    $variantBin = Join-Path $moduleDir "bin\$variant"
    if (Test-Path -LiteralPath $builtPdb) {
        New-Item -ItemType Directory -Path $variantBin -Force | Out-Null
        $pdbDst = Join-Path $variantBin 'BetterBannerlordNavmeshing.pdb'
        Copy-Item -LiteralPath $builtPdb -Destination $pdbDst -Force
        Write-Host "  PDB -> $pdbDst"
    } else {
        Write-Host "  WARNING: PDB not found at $builtPdb"
    }
    Write-Host ""
}

# Deploy the canonical module descriptor next to the built DLLs.
$subModuleSrc = Join-Path $repo 'SubModule.xml'
$subModuleDst = Join-Path $moduleDir 'SubModule.xml'
Copy-Item -LiteralPath $subModuleSrc -Destination $subModuleDst -Force
Write-Host "SubModule.xml -> $subModuleDst"
Write-Host ""

Write-Host "Summary"
foreach ($variant in $variants) {
    $dll = Join-Path $moduleDir "bin\$variant\BetterBannerlordNavmeshing.dll"
    $pdb = Join-Path $moduleDir "bin\$variant\BetterBannerlordNavmeshing.pdb"
    if (Test-Path -LiteralPath $dll) {
        $bytes = (Get-Item -LiteralPath $dll).Length
        Write-Host ("  OK       {0}\BetterBannerlordNavmeshing.dll ({1} bytes)" -f $variant, $bytes)
    } else {
        Write-Host ("  MISSING  {0}\BetterBannerlordNavmeshing.dll" -f $variant)
    }
    if (Test-Path -LiteralPath $pdb) {
        $pbytes = (Get-Item -LiteralPath $pdb).Length
        Write-Host ("  OK       {0}\BetterBannerlordNavmeshing.pdb ({1} bytes)" -f $variant, $pbytes)
    } else {
        Write-Host ("  MISSING  {0}\BetterBannerlordNavmeshing.pdb" -f $variant)
    }
}
Write-Host "  SubModule.xml present: $([bool](Test-Path -LiteralPath $subModuleDst))"
Write-Host "Done."
