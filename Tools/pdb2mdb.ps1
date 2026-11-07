# Converts the Windows .pdb of a built assembly to a Mono .mdb (<name>.dll.mdb).
# Unity Mono ignores Windows .pdb files; with the .mdb next to the DLL, stack traces
# show file and line numbers. Uses the Mono.Cecil copy that ships with BepInEx.
param(
    [Parameter(Mandatory = $true)][string]$Assembly,
    [Parameter(Mandatory = $true)][string]$CecilDir,
    [string]$SearchDirs = ""
)

$ErrorActionPreference = 'Stop'

Add-Type -Path (Join-Path $CecilDir 'Mono.Cecil.dll')
Add-Type -Path (Join-Path $CecilDir 'Mono.Cecil.Pdb.dll')
Add-Type -Path (Join-Path $CecilDir 'Mono.Cecil.Mdb.dll')

$Assembly = (Resolve-Path $Assembly).Path
$dir = Split-Path $Assembly
$name = Split-Path $Assembly -Leaf

$resolver = New-Object Mono.Cecil.DefaultAssemblyResolver
$resolver.AddSearchDirectory($dir)
foreach ($d in ($SearchDirs -split ";")) { if ($d -and (Test-Path $d)) { $resolver.AddSearchDirectory($d) } }

$rp = New-Object Mono.Cecil.ReaderParameters
$rp.ReadSymbols = $true
$rp.SymbolReaderProvider = New-Object Mono.Cecil.Pdb.PdbReaderProvider
$rp.AssemblyResolver = $resolver

# Cecil writes the .mdb next to the DLL it writes, so write a throwaway copy in a temp folder.
# The MVID and IL are kept, so the .mdb matches the original DLL.
$tmp = Join-Path ([IO.Path]::GetTempPath()) ('pdb2mdb_' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp | Out-Null
try {
    $asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($Assembly, $rp)
    try {
        $wp = New-Object Mono.Cecil.WriterParameters
        $wp.WriteSymbols = $true
        $wp.SymbolWriterProvider = New-Object Mono.Cecil.Mdb.MdbWriterProvider
        $asm.Write((Join-Path $tmp $name), $wp)
    }
    finally { $asm.Dispose() }
    Copy-Item (Join-Path $tmp "$name.mdb") (Join-Path $dir "$name.mdb") -Force
    Write-Host "pdb2mdb: $name.mdb"
}
finally { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue }
