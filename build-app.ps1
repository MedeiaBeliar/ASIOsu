param([ValidateSet('Debug','Release')][string]$Configuration='Release')
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
& (Join-Path $root 'tools\build-icon.ps1')
& (Join-Path $root 'build-native.ps1') -Configuration $Configuration
& (Join-Path $root 'build-loopback.ps1')
$artifacts=Join-Path $root 'artifacts'
$publish=Join-Path $artifacts 'publish'
$appOutput=Join-Path $artifacts 'app'
$probeOutput=Join-Path $artifacts 'probe'
$stableProbeOutput=Join-Path $artifacts 'probe-stable'
function Assert-Child([string]$Path){
    $full=[IO.Path]::GetFullPath($Path);$base=[IO.Path]::GetFullPath($artifacts)+[IO.Path]::DirectorySeparatorChar
    if(!$full.StartsWith($base,[StringComparison]::OrdinalIgnoreCase)){throw "Build path escaped artifacts: $full"}
}
foreach($path in @($publish,$appOutput,$probeOutput,$stableProbeOutput)){
    Assert-Child $path
    if(Test-Path -LiteralPath $path){Remove-Item -LiteralPath $path -Recurse -Force}
    New-Item -ItemType Directory -Path $path -Force | Out-Null
}
dotnet publish (Join-Path $root 'app\ASIOsu\ASIOsu.csproj') -c $Configuration -p:Platform=x64 -r win-x64 --self-contained true -o $appOutput
if($LASTEXITCODE -ne 0){throw 'WinUI application publish failed.'}
dotnet publish (Join-Path $root 'probe\ASIOsu.Probe\ASIOsu.Probe.csproj') -c $Configuration -r win-x64 --self-contained true -o $probeOutput
if($LASTEXITCODE -ne 0){throw 'ASIO probe publish failed.'}
dotnet publish (Join-Path $root 'probe\ASIOsu.Probe\ASIOsu.Probe.csproj') -c $Configuration -p:PlatformTarget=x86 -r win-x86 --self-contained true -o $stableProbeOutput
if($LASTEXITCODE -ne 0){throw 'Stable ASIO probe publish failed.'}

$runtime=Join-Path $publish 'runtime'
New-Item -ItemType Directory -Path $runtime -Force | Out-Null
Copy-Item -Path (Join-Path $appOutput '*') -Destination $runtime -Recurse -Force
$tools=Join-Path $runtime 'tools';New-Item -ItemType Directory -Path $tools -Force | Out-Null
Copy-Item -Path (Join-Path $probeOutput '*') -Destination $tools -Recurse -Force
Copy-Item -LiteralPath (Join-Path $root 'artifacts\native\bassasio.dll') -Destination $tools -Force
$stableTools=Join-Path $tools 'stable';New-Item -ItemType Directory -Path $stableTools -Force | Out-Null
Copy-Item -Path (Join-Path $stableProbeOutput '*') -Destination $stableTools -Recurse -Force
Copy-Item -LiteralPath (Join-Path $root 'artifacts\stable-loopback\bassasio.dll') -Destination $stableTools -Force
Copy-Item -LiteralPath (Join-Path $root 'artifacts\stable-loopback\ASIOsu.Loopback.exe') -Destination $stableTools -Force
$payload=Join-Path $runtime 'payload';New-Item -ItemType Directory -Path $payload -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'artifacts\native\basswasapi.dll') -Destination $payload -Force
Copy-Item -LiteralPath (Join-Path $root 'artifacts\native\bassasio.dll') -Destination $payload -Force
Copy-Item -LiteralPath (Join-Path $root 'third_party\un4seen\bassasio.txt') -Destination $payload -Force

$vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vs=& $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
$msvc=Get-ChildItem -LiteralPath (Join-Path $vs 'VC\Tools\MSVC') -Directory | Sort-Object Name -Descending | Select-Object -First 1 -ExpandProperty FullName
$sdk=Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10'
$sdkVersion=Get-ChildItem -LiteralPath (Join-Path $sdk 'Include') -Directory | Sort-Object Name -Descending | Select-Object -First 1 -ExpandProperty Name
$env:INCLUDE="$msvc\include;$sdk\Include\$sdkVersion\ucrt;$sdk\Include\$sdkVersion\shared;$sdk\Include\$sdkVersion\um"
$env:LIB="$msvc\lib\x64;$sdk\Lib\$sdkVersion\ucrt\x64;$sdk\Lib\$sdkVersion\um\x64"
$compiler=Join-Path $msvc 'bin\Hostx64\x64\cl.exe'
$resourceCompiler=Join-Path $sdk "bin\$sdkVersion\x64\rc.exe"
& $resourceCompiler /nologo "/fo$(Join-Path $artifacts 'launcher.res')" (Join-Path $root 'native\launcher.rc')
if($LASTEXITCODE -ne 0){throw 'Root launcher icon build failed.'}
& $compiler /nologo /std:c++20 /EHsc /O2 /MT /W4 /WX "/Fo:$(Join-Path $artifacts 'launcher.obj')" (Join-Path $root 'native\launcher.cpp') (Join-Path $artifacts 'launcher.res') `
    "/Fe:$(Join-Path $publish 'ASIOsu.exe')" shell32.lib user32.lib /link /SUBSYSTEM:WINDOWS
if($LASTEXITCODE -ne 0){throw 'Root launcher build failed.'}
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination $publish
Copy-Item -LiteralPath (Join-Path $root 'THIRD_PARTY_NOTICES.md') -Destination $publish
if(Test-Path -LiteralPath (Join-Path $root 'README.md')){Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $publish}
$rootExecutables=@(Get-ChildItem -LiteralPath $publish -File -Filter '*.exe')
if($rootExecutables.Count -ne 1 -or $rootExecutables[0].Name -ne 'ASIOsu.exe'){throw 'Package root must contain one launcher executable.'}
Write-Output "PASS: portable application published at $publish"

