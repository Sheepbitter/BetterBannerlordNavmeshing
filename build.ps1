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

# Provision Modules\BetterBannerlordNavmeshing\NavMeshPrefabs.
#
# The engine only ever looks for navmesh prefabs in a Modules\<Module>\NavMeshPrefabs
# folder, and it builds its name -> module index once, while the modules load
# (TaleWorlds.Native.dll rva 0x4EBCE0: it walks the modules the engine LOADED -
# the _MODULES_*A*B*_MODULES_ list, i.e. Utilities.GetModulesNames() - and
# registers every *.bin stem found in an existing NavMeshPrefabs folder). Two
# things follow, and this folder has to exist because of both:
#
#   * A folder created after that point is invisible for the rest of the session,
#     which is what makes a procedural generator log
#       No navigation mesh prefab was found with name "<name>"!
#     (native rva 0x9795F0, reached via Scene.ImportNavigationMeshPrefab).
#   * This module is the fallback home for scenes whose own module is NOT loaded
#     in the editor (TerrainNavMeshGenerator.WriteAndImport writes the same bin
#     here as well, then skips the import and says so). The copy only becomes
#     resolvable from the NEXT editor start, because the index is built at
#     start-up - so the folder must exist before start-up, not after.
#
# The folder is therefore created here, at deploy time, so it exists before the
# engine ever starts. A .txt inside it is not a *.bin, so it registers nothing; it
# only documents the folder for whoever looks at the module folder later.
$navMeshPrefabsDir = Join-Path $moduleDir 'NavMeshPrefabs'
New-Item -ItemType Directory -Path $navMeshPrefabsDir -Force | Out-Null
$navMeshPrefabsReadme = Join-Path $navMeshPrefabsDir 'README.txt'
if (-not (Test-Path -LiteralPath $navMeshPrefabsReadme)) {
    $navMeshPrefabsText = @'
Navmesh prefabs are read from this folder.

The engine resolves a navmesh prefab name to a module by scanning
Modules\<Module>\NavMeshPrefabs\*.bin once, while the modules are loading, and it
keeps that list for the whole session. It only scans the modules the game/editor
actually loaded. Three consequences:

  * This folder must exist before the game/editor starts. If it is created later
    (for example by the first export that resolves to this module), every bin
    written into it stays invisible until the index is rebuilt - restart the
    editor or reload modules.
  * A prefab name that did not exist when the index was built can never be
    resolved in that session, however many times its .bin is rewritten.
  * A bin that sits in a module that is NOT loaded in this session can never be
    resolved, in this session or any later one, until that module is enabled.
    build.ps1 creates this folder; TerrainNavMeshGenerator writes <prefab>.bin
    here as well in that case (and still keeps the canonical copy in the scene's
    own module), then logs "import SKIPPED" and asks for one restart before
    Import Only works.
'@
    Set-Content -LiteralPath $navMeshPrefabsReadme -Value $navMeshPrefabsText -Encoding UTF8
}
Write-Host "NavMeshPrefabs -> $navMeshPrefabsDir (pre-existing folder, so the engine indexes it)"
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
