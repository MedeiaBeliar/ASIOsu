param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'artifacts\stable-loopback'))
$ErrorActionPreference = 'Stop'
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
$msvc = Get-ChildItem -LiteralPath (Join-Path $vs 'VC\Tools\MSVC') -Directory |
    Sort-Object Name -Descending | Select-Object -First 1 -ExpandProperty FullName
$sdk = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10'
$version = Get-ChildItem -LiteralPath (Join-Path $sdk 'Include') -Directory |
    Sort-Object Name -Descending | Select-Object -First 1 -ExpandProperty Name
$env:INCLUDE = "$msvc\include;$sdk\Include\$version\ucrt;$sdk\Include\$version\shared;$sdk\Include\$version\um;$sdk\Include\$version\winrt"
$env:LIB = "$msvc\lib\x86;$sdk\Lib\$version\ucrt\x86;$sdk\Lib\$version\um\x86"
$include = Join-Path $PSScriptRoot 'third_party\un4seen\c'
$library = Join-Path $include 'x86\bassasio.lib'
$arguments = @('/nologo','/std:c++20','/EHsc','/W4','/WX','/O2','/MT',"/I$include",
    "/Fo:$(Join-Path $output 'stable_loopback.obj')", (Join-Path $PSScriptRoot 'native\stable_loopback.cpp'),
    "/Fe:$(Join-Path $output 'ASIOsu.Loopback.exe')", '/link', $library, 'mmdevapi.lib', 'ole32.lib', 'winmm.lib')
& (Join-Path $msvc 'bin\Hostx64\x86\cl.exe') @arguments
if ($LASTEXITCODE -ne 0) { throw 'ASIO loopback build failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'third_party\un4seen\x86\bassasio.dll') -Destination $output -Force
