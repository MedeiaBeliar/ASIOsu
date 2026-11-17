param([ValidateSet('Debug','Release')][string]$Configuration='Release')
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
& (Join-Path $root 'build-app.ps1') -Configuration $Configuration
$stage=Join-Path $root 'artifacts\publish'
$version=[Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $stage 'runtime\ASIOsu.dll'))
$releaseVersion="$($version.FileMajorPart).$($version.FileMinorPart).$($version.FileBuildPart)"
$manifestFiles=@(Get-ChildItem -LiteralPath $stage -File -Recurse | Where-Object {$_.Name -ne 'manifest.json'} | ForEach-Object {
    @{path=$_.FullName.Substring($stage.Length+1);size=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
})
@{product='ASIOsu';version=$releaseVersion;target='Windows x64';built=[DateTimeOffset]::Now.ToString('O');files=$manifestFiles} |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $stage 'manifest.json') -Encoding utf8
$dist=Join-Path $root 'dist';New-Item -ItemType Directory -Path $dist -Force | Out-Null
$zip=Join-Path $dist 'ASIOsu-win-x64.zip'
if(Test-Path -LiteralPath $zip){Remove-Item -LiteralPath $zip -Force}
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal
$verify=Join-Path $root ('artifacts\verify-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $verify | Out-Null
Expand-Archive -LiteralPath $zip -DestinationPath $verify
$manifest=Get-Content -LiteralPath (Join-Path $verify 'manifest.json') -Raw | ConvertFrom-Json
foreach($entry in $manifest.files){
    $file=Join-Path $verify $entry.path
    if(!(Test-Path -LiteralPath $file) -or (Get-Item -LiteralPath $file).Length -ne [long]$entry.size -or
       (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $entry.sha256){throw "Package verification failed: $($entry.path)"}
}
Write-Output "PASS: package verified at $zip"

