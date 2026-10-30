<#
.SYNOPSIS
  Downloads the pinned fonts of tools/fonts/fonts.json, checks their SHA-256, subsets Noto Sans SC with fonttools
  and copies everything into src/Fadenstack.UI.Avalonia/Assets/Fonts/ (22 section 3.4).
.NOTES
  Needs Python with fonttools and brotli: python -m pip install --user fonttools brotli
  Inter is not fetched: it comes from the Avalonia.Fonts.Inter NuGet package.
#>
[CmdletBinding()]
param(
    [string]$Config = (Join-Path $PSScriptRoot 'fonts.json'),
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
)
$ErrorActionPreference = 'Stop'

function Get-Sha256([string]$Path) { (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant() }
function Assert-Sha256([string]$Path, [string]$Expected, [string]$What) {
    $actual = Get-Sha256 $Path
    if ($actual -ne $Expected.ToLowerInvariant()) { throw "SHA-256 mismatch for ${What}: expected $Expected, got $actual" }
}

$cfg = Get-Content -Raw -LiteralPath $Config | ConvertFrom-Json
$out = Join-Path $RepoRoot $cfg.outputDir
$tmp = Join-Path ([IO.Path]::GetTempPath()) ('fadenstack-fonts-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp, $out, (Join-Path $out 'licenses') -Force | Out-Null
try {
    $files = @{}
    foreach ($s in $cfg.sources) {
        $dest = Join-Path $tmp $s.id
        Write-Host "download $($s.id) ($($s.version))"
        # GitHub resets connections now and then: try a few times.
        for ($try = 1; $try -le 5; $try++) {
            try { Invoke-WebRequest -Uri $s.url -OutFile $dest -UseBasicParsing; break }
            catch { if ($try -eq 5) { throw }; Start-Sleep -Seconds (2 * $try) }
        }
        Assert-Sha256 $dest $s.sha256 $s.id
        $files[$s.id] = $dest
    }

    foreach ($c in $cfg.copies) { Copy-Item -LiteralPath $files[$c.from] -Destination (Join-Path $out $c.to) -Force }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    foreach ($z in $cfg.zipEntries) {
        $zip = [IO.Compression.ZipFile]::OpenRead($files[$z.from])
        try {
            $entry = $zip.GetEntry($z.entry)
            if (-not $entry) { throw "$($z.entry) not found in $($z.from)" }
            $dest = Join-Path $out $z.to
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $dest, $true)
            Assert-Sha256 $dest $z.sha256 $z.entry
        } finally { $zip.Dispose() }
    }

    $text = Join-Path $tmp 'gb2312.txt'
    python (Join-Path $PSScriptRoot 'subset_text.py') $text
    if ($LASTEXITCODE -ne 0) { throw 'subset_text.py failed' }
    foreach ($s in $cfg.subsets) {
        $dest = Join-Path $out $s.to
        # Layout features are dropped to what Latin and CJK text need; names are kept so the family stays "Noto Sans SC".
        python -m fontTools.subset $files[$s.from] "--text-file=$text" "--unicodes=$($cfg.subset.unicodeRanges)" `
            "--output-file=$dest" --layout-features='kern,liga,locl,vert,vrt2,palt,halt' --name-IDs='*' --notdef-outline --no-hinting --desubroutinize
        if ($LASTEXITCODE -ne 0) { throw "pyftsubset failed for $($s.from)" }
        python (Join-Path $PSScriptRoot 'fix_names.py') $dest
        if ($LASTEXITCODE -ne 0) { throw "fix_names.py failed for $($s.to)" }
        Write-Host ("subset {0}: {1:N0} bytes" -f $s.to, (Get-Item $dest).Length)
    }
    Write-Host "Fonts written to $out"
} finally {
    Remove-Item -Recurse -Force -LiteralPath $tmp -ErrorAction SilentlyContinue
}
