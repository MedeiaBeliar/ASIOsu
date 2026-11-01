param([ValidateSet('Debug','Release')][string]$Configuration='Release')
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
$vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$install=& $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if(!$install){throw 'Visual Studio C++ build tools were not found.'}
$toolRoot=Get-ChildItem -LiteralPath (Join-Path $install 'VC\Tools\MSVC') -Directory | Sort-Object Name -Descending | Select-Object -First 1
$cl=Join-Path $toolRoot.FullName 'bin\Hostx64\x64\cl.exe'
$link=Join-Path $toolRoot.FullName 'bin\Hostx64\x64\link.exe'
if(!(Test-Path -LiteralPath $cl)-or !(Test-Path -LiteralPath $link)){throw 'The x64 C++ compiler is incomplete.'}
$windowsSdk=Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10'
$sdkVersion=Get-ChildItem -LiteralPath (Join-Path $windowsSdk 'Include') -Directory | Sort-Object Name -Descending | Select-Object -First 1 -ExpandProperty Name
$env:INCLUDE="$($toolRoot.FullName)\include;$windowsSdk\Include\$sdkVersion\ucrt;$windowsSdk\Include\$sdkVersion\shared;$windowsSdk\Include\$sdkVersion\um"
$env:LIB="$($toolRoot.FullName)\lib\x64;$windowsSdk\Lib\$sdkVersion\ucrt\x64;$windowsSdk\Lib\$sdkVersion\um\x64"
$out=Join-Path $root 'artifacts\native'
New-Item -ItemType Directory -Path $out -Force | Out-Null
$include=Join-Path $root 'third_party\un4seen\c'
$library=Join-Path $include 'x64'
$optimise=if($Configuration -eq 'Release'){'/O2'}else{'/Od'}
& $cl /nologo /std:c++20 /EHsc /W4 /WX $optimise /MD /LD /DUNICODE /D_UNICODE "/I$include" "/Fo:$(Join-Path $out 'asio_bridge.obj')" `
    (Join-Path $root 'native\asio_bridge.cpp') "/link" "/DEF:$(Join-Path $root 'native\basswasapi.def')" `
    "/LIBPATH:$library" bassasio.lib "/OUT:$(Join-Path $out 'basswasapi.dll')" "/IMPLIB:$(Join-Path $out 'basswasapi.lib')"
if($LASTEXITCODE -ne 0){throw "Native build failed with exit code $LASTEXITCODE."}
Copy-Item -LiteralPath (Join-Path $root 'third_party\un4seen\x64\bassasio.dll') -Destination (Join-Path $out 'bassasio.dll') -Force
& $cl /nologo /std:c++20 /EHsc /W4 /WX $optimise /MT /DUNICODE /D_UNICODE "/I$include" "/Fo:$(Join-Path $out 'native_contract.obj')" `
    (Join-Path $root 'tests\native_contract.cpp') "/link" "/LIBPATH:$library" bassasio.lib `
    ole32.lib "/OUT:$(Join-Path $out 'native_contract.exe')"
if($LASTEXITCODE -ne 0){throw "Native contract test build failed with exit code $LASTEXITCODE."}
New-Item -ItemType Directory -Path (Join-Path $out 'fake') -Force | Out-Null
& $cl /nologo /std:c++20 /EHsc /W4 /WX $optimise /MT /LD /DUNICODE /D_UNICODE "/I$include" `
    "/Fo:$(Join-Path $out 'fake_asio.obj')" (Join-Path $root 'tests\fake_asio.cpp') "/link" `
    "/OUT:$(Join-Path $out 'fake\bassasio.dll')" "/IMPLIB:$(Join-Path $out 'fake\bassasio.lib')"
if($LASTEXITCODE -ne 0){throw "Fake ASIO build failed with exit code $LASTEXITCODE."}
& $cl /nologo /std:c++20 /EHsc /W4 /WX $optimise /MT /DUNICODE /D_UNICODE "/I$include" `
    "/Fo:$(Join-Path $out 'bridge_active.obj')" (Join-Path $root 'tests\bridge_active.cpp') "/link" `
    "/OUT:$(Join-Path $out 'bridge_active.exe')"
if($LASTEXITCODE -ne 0){throw "Bridge active test build failed with exit code $LASTEXITCODE."}
$fixture=Join-Path $out 'bridge-active-fixture'
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $out 'basswasapi.dll') -Destination $fixture -Force
Copy-Item -LiteralPath (Join-Path $out 'fake\bassasio.dll') -Destination $fixture -Force
& (Join-Path $out 'bridge_active.exe') $fixture
if($LASTEXITCODE -ne 0){throw "Bridge active-path test failed with exit code $LASTEXITCODE."}
Write-Output "PASS: independent native bridge built at $out"

