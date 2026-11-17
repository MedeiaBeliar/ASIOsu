param([string]$Launcher = (Join-Path $PSScriptRoot '..\artifacts\publish\ASIOsu.exe'))
$ErrorActionPreference = 'Stop'
$launcherPath = (Resolve-Path -LiteralPath $Launcher).ProviderPath
$fixture = [IO.Directory]::CreateTempSubdirectory('asiosu-update-cwd-').FullName
try {
    $target = Join-Path $fixture 'installed'
    $package = Join-Path $fixture 'package'
    $runtime = Join-Path $target 'runtime'
    New-Item -ItemType Directory -Path $runtime,(Join-Path $package 'runtime') | Out-Null
    Copy-Item -LiteralPath $launcherPath -Destination (Join-Path $package 'ASIOsu.exe')
    Copy-Item -LiteralPath $launcherPath -Destination (Join-Path $package 'runtime\ASIOsu.exe')
    [IO.File]::WriteAllText((Join-Path $package 'manifest.json'), '{}')
    [IO.File]::WriteAllText((Join-Path $runtime 'old.txt'), 'old runtime')
    [IO.File]::WriteAllText((Join-Path $package 'runtime\new.txt'), 'new runtime')

    $previousDirectory = [IO.Directory]::GetCurrentDirectory()
    try {
        [IO.Directory]::SetCurrentDirectory($runtime)
        $blocked = $false
        try { [IO.Directory]::Move($runtime, (Join-Path $target 'unexpected-move')) }
        catch [IO.IOException] { $blocked = $true }
        catch [UnauthorizedAccessException] { $blocked = $true }
        if (!$blocked) { throw 'The current-directory lock was not reproduced.' }
    } finally { [IO.Directory]::SetCurrentDirectory($previousDirectory) }

    $start = [Diagnostics.ProcessStartInfo]::new($launcherPath)
    $start.WorkingDirectory = $runtime
    $start.UseShellExecute = $false
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    foreach ($argument in '--apply-update','--source',$package,'--target',$target,'--wait-pid','0','--tag','test','--no-restart') {
        $start.ArgumentList.Add($argument)
    }
    $hostProcess = [Diagnostics.Process]::Start($start)
    try {
        if (!$hostProcess.WaitForExit(15000)) {
            $hostProcess.Kill()
            $hostProcess.WaitForExit()
            throw 'The updater did not exit.'
        }
        if ($hostProcess.ExitCode -ne 0) { throw "Updater failed with exit code $($hostProcess.ExitCode)." }
    } finally { $hostProcess.Dispose() }

    if (![IO.File]::Exists((Join-Path $target 'runtime\new.txt')) -or
        [IO.File]::Exists((Join-Path $target 'runtime\old.txt')) -or
        [IO.Directory]::Exists((Join-Path $target '.asiosu-update-backup'))) {
        throw 'The updater did not fully replace the runtime.'
    }
    'PASS: the update host releases its inherited runtime directory and completes the swap.'
} finally {
    [IO.Directory]::Delete($fixture, $true)
}
