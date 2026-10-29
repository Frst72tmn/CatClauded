<#
.SYNOPSIS
  Сборка релиза CatClauded: тесты, self-contained single-file для win-x64 и linux-x64,
  папки release\<версия>\<rid>\ и архивы CatClauded-<версия>-win-x64.zip / CatClauded-<версия>-linux-x64.tar.gz.
.EXAMPLE
  pwsh scripts/release.ps1
  pwsh scripts/release.ps1 -Version 0.2.0 -SkipTests
#>
param(
    [string]$Version,
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $Version) {
    $Version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
$out = Join-Path $root "release\$Version"
Write-Host "CatClauded $Version → $out"

if (-not $SkipTests) {
    dotnet test (Join-Path $root 'tests\CatClauded.Tests') -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Тесты не прошли' }
}

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory $out | Out-Null

foreach ($rid in 'win-x64', 'linux-x64') {
    $dir = Join-Path $out $rid
    dotnet publish (Join-Path $root 'src\CatClauded\CatClauded.csproj') -c Release -r $rid --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
        -p:DebugType=none -p:Version=$Version -o $dir
    if ($LASTEXITCODE -ne 0) { throw "Сборка $rid не удалась" }
    Copy-Item (Join-Path $root 'README.md') $dir
}

# Windows — zip.
$zip = Join-Path $out "CatClauded-$Version-win-x64.zip"
Compress-Archive -Path (Join-Path $out 'win-x64\*') -DestinationPath $zip -Force

# Linux — tar.gz с правом на запуск (tar из Windows его не ставит, поэтому через System.Formats.Tar).
$tgz = Join-Path $out "CatClauded-$Version-linux-x64.tar.gz"
$file = [IO.File]::Create($tgz)
$gzip = [IO.Compression.GZipStream]::new($file, [IO.Compression.CompressionLevel]::Optimal)
$tar = [Formats.Tar.TarWriter]::new($gzip, [Formats.Tar.TarEntryFormat]::Pax, $false)
try {
    Get-ChildItem (Join-Path $out 'linux-x64') -File | ForEach-Object {
        $entry = [Formats.Tar.PaxTarEntry]::new([Formats.Tar.TarEntryType]::RegularFile, "CatClauded-$Version/$($_.Name)")
        $entry.Mode = if ($_.Name -eq 'CatClauded') { [IO.UnixFileMode]'UserRead, UserWrite, UserExecute, GroupRead, GroupExecute, OtherRead, OtherExecute' }
                      else { [IO.UnixFileMode]'UserRead, UserWrite, GroupRead, OtherRead' }
        $entry.ModificationTime = $_.LastWriteTimeUtc
        $stream = $_.OpenRead()
        try { $entry.DataStream = $stream; $tar.WriteEntry($entry) } finally { $stream.Dispose() }
    }
}
finally { $tar.Dispose(); $gzip.Dispose(); $file.Dispose() }

Get-ChildItem $out -File | ForEach-Object { '{0,-45} {1,8:N1} МБ' -f $_.Name, ($_.Length / 1MB) }
