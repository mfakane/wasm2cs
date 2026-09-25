param(
    [string]$Editor = "C:\Program Files\Unity\Hub\Editor\6000.6.0f1\Editor\Unity.exe",
    [string]$WorkDirectory = (Join-Path $env:TEMP ("wasm2cs-sh13-" + [guid]::NewGuid().ToString("N"))),
    [int]$TimeoutMinutes = 240
)
$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent
$project = Join-Path $WorkDirectory "SelfHost"
$generatedProject = Join-Path $WorkDirectory "Generated"
$prepare = Join-Path $WorkDirectory "prepare"
$package = Join-Path $WorkDirectory "com.mfakane.wasm2cs.tgz"
$timeoutMs = $TimeoutMinutes * 60 * 1000
if (!(Test-Path $Editor)) { throw "Unity Editor is missing: $Editor" }

function Invoke-SelfHosting([string[]]$Arguments) {
    Push-Location $repo
    try {
        & node (Join-Path $repo "scripts/self-hosting.mjs") @Arguments
        if ($LASTEXITCODE -ne 0) { throw "self-hosting $($Arguments -join ' ') failed." }
    } finally { Pop-Location }
}
function Sha256([string]$Path) {
    $sha = [Security.Cryptography.SHA256]::Create()
    $stream = [IO.File]::OpenRead($Path)
    try { return ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace("-", "").ToLowerInvariant() }
    finally { $stream.Dispose(); $sha.Dispose() }
}
function RunEditor([string]$ProjectPath, [string[]]$Arguments, [string]$LogName) {
    Write-Output "Unity: $LogName"
    $log = Join-Path $WorkDirectory $LogName
    $all = @("-batchmode", "-nographics", "-projectPath", "`"$ProjectPath`"", "-logFile", "`"$log`"") + $Arguments
    $process = Start-Process -FilePath $Editor -ArgumentList $all -PassThru
    if (!$process.WaitForExit($timeoutMs)) { $process.Kill(); throw "Unity timed out; see $log" }
    if ($process.ExitCode -ne 0) { Get-Content $log -Tail 80; throw "Unity exited $($process.ExitCode); see $log" }
}
function Set-StackReserve([string]$Exe) {
    # ponytail: IL2CPP frames overflow the 1MB default during mono_wasm_load_runtime. 32MB reserve.
    $bytes = [IO.File]::ReadAllBytes($Exe)
    $pe = [BitConverter]::ToInt32($bytes, 0x3C)
    if ([BitConverter]::ToUInt16($bytes, $pe + 24) -ne 0x20B) { throw "Unexpected PE magic in $Exe" }
    [BitConverter]::GetBytes([uint64]0x2000000).CopyTo($bytes, $pe + 24 + 72)
    [IO.File]::WriteAllBytes($Exe, $bytes)
}
function RunSelfHost([string]$Exe, [string]$Scenario, [string]$OutDir, [string]$LogName) {
    New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
    $env:SH10_SCENARIO = $Scenario
    $env:WASM2CS_SELFHOST_OUT = $OutDir
    $log = Join-Path $WorkDirectory $LogName
    $player = Start-Process $Exe -ArgumentList @("-batchmode", "-nographics", "-logFile", "`"$log`"", "-selfHostScenario", $Scenario, "-selfHostOut", $OutDir) -PassThru
    if (!$player.WaitForExit($timeoutMs)) { $player.Kill(); throw "Player timed out; see $log" }
    Set-Content -Path (Join-Path $OutDir "process-exit.txt") -Value $player.ExitCode
    if ($player.ExitCode -ne 0) { Get-Content $log -Tail 80; throw "Player exited $($player.ExitCode); see $log" }
}
function InstallPackage([string]$ProjectPath, [string]$LogName) {
    New-Item -ItemType Directory -Path (Join-Path $ProjectPath "Assets/Editor") -Force | Out-Null
    Copy-Item (Join-Path $repo "unity/Bootstrap/PackageInstaller.cs") (Join-Path $ProjectPath "Assets/Editor/PackageInstaller.cs")
    $env:WASM2CS_UNITY_PACKAGE = $package.Replace("\", "/")
    RunEditor $ProjectPath @("-executeMethod", "PackageInstaller.Install") $LogName
}

New-Item -ItemType Directory -Path $WorkDirectory -Force | Out-Null
Write-Output "Unity self-host artifacts: $WorkDirectory"
$expectedPackage = (Get-Content (Join-Path $repo "docs/self-hosting/SH-12.5-unity.json") -Raw | ConvertFrom-Json).packageSha256
$packageSource = Join-Path $repo "artifacts/com.mfakane.wasm2cs-0.1.0-preview.1.tgz"
if ((Sha256 $packageSource) -ne $expectedPackage) { throw "Unity package hash differs from SH-12.5 evidence." }
Copy-Item $packageSource $package
Invoke-SelfHosting @("unity-prepare", "--output", $prepare)

$hostOut = Join-Path $WorkDirectory "host"
New-Item -ItemType Directory -Path $hostOut -Force | Out-Null
dotnet build (Join-Path $repo "src/Wasm2Cs.DotnetHost/Wasm2Cs.DotnetHost.csproj") -f netstandard2.0 -c Release -o $hostOut --nologo
if ($LASTEXITCODE -ne 0) { throw "Host library build failed." }
$hostDll = Join-Path $hostOut "Wasm2Cs.DotnetHost.dll"

RunEditor $project @("-createProject", "`"$project`"", "-quit") "create.log"
InstallPackage $project "install.log"
$runtime = Join-Path $project "Assets/SelfHostRuntime"
New-Item -ItemType Directory -Path $runtime, (Join-Path $project "Assets/SelfHost/Editor"), (Join-Path $project "Assets/Plugins"), (Join-Path $project "Assets/Resources/Guest") -Force | Out-Null
$nativeErrors = $PSNativeCommandUseErrorActionPreference
$PSNativeCommandUseErrorActionPreference = $false
& robocopy (Join-Path $repo "artifacts/self-hosting/generated") $runtime *.g.cs /NFL /NDL /NJH /NJS /nc /ns /np
$copyExit = $LASTEXITCODE
$PSNativeCommandUseErrorActionPreference = $nativeErrors
if ($copyExit -ge 8) { throw "Generated source copy failed: $copyExit" }
$global:LASTEXITCODE = 0
$prepared = Get-Content (Join-Path $prepare "prepare.json") -Raw | ConvertFrom-Json
$copied = (Get-ChildItem $runtime -Filter *.g.cs).Count
if ($copied -ne $prepared.generatedEntries) { throw "Copied $copied generated sources; expected $($prepared.generatedEntries)." }
# Unity deletes Assets/*.g.cs as generator output after the first domain reload.
Get-ChildItem $runtime -Filter *.g.cs | ForEach-Object { Rename-Item $_.FullName ($_.Name -replace '\.g\.cs$', '.cs') }
Copy-Item (Join-Path $repo "unity/SelfHost/Wasm2Cs.SelfHost.Runtime.asmdef") (Join-Path $runtime "Wasm2Cs.SelfHost.Runtime.asmdef")
Copy-Item (Join-Path $repo "unity/SelfHost/UnitySelfHostAdapter.cs") (Join-Path $project "Assets/SelfHost/UnitySelfHostAdapter.cs")
Copy-Item (Join-Path $repo "unity/SelfHost/UnitySelfHostEntry.cs") (Join-Path $project "Assets/SelfHost/UnitySelfHostEntry.cs")
Copy-Item (Join-Path $prepare "UnitySelfHostRunner.cs") (Join-Path $project "Assets/SelfHost/UnitySelfHostRunner.cs")
Copy-Item (Join-Path $repo "unity/SelfHost/Wasm2Cs.SelfHost.asmdef") (Join-Path $project "Assets/SelfHost/Wasm2Cs.SelfHost.asmdef")
Copy-Item (Join-Path $repo "unity/SelfHost/Editor/Wasm2Cs.SelfHost.Editor.asmdef") (Join-Path $project "Assets/SelfHost/Editor/Wasm2Cs.SelfHost.Editor.asmdef")
Copy-Item (Join-Path $repo "unity/SelfHost/Editor/GuestAssemblyCheck.cs") (Join-Path $project "Assets/SelfHost/Editor/GuestAssemblyCheck.cs")
Copy-Item (Join-Path $repo "unity/SelfHost/Editor/SelfHostBuild.cs") (Join-Path $project "Assets/SelfHost/Editor/SelfHostBuild.cs")
Copy-Item (Join-Path $repo "unity/SelfHost/link.xml") (Join-Path $project "Assets/link.xml")
Copy-Item $hostDll (Join-Path $project "Assets/Plugins/Wasm2Cs.DotnetHost.dll")
$guest = Join-Path $project "Assets/Resources/Guest"
foreach ($line in Get-Content (Join-Path $prepare "guest-manifest.txt")) {
    if (!$line) { continue }
    $name, $hash = $line.Split(" ", 2)
    $source = Join-Path $repo "artifacts/self-hosting/bundle/_framework/$name"
    $dest = Join-Path $guest "$name.bytes"
    Copy-Item $source $dest
    if ((Sha256 $dest) -ne $hash) { throw "Copied guest asset hash differs: $name" }
}
Copy-Item (Join-Path $prepare "guest-manifest.txt") (Join-Path $guest "manifest.txt")

RunEditor $project @("-executeMethod", "GuestAssemblyCheck.Verify", "-quit") "guest-data.log"
$editorOut = Join-Path $WorkDirectory "editor-translate"
$env:SH10_SCENARIO = "translate"
$env:WASM2CS_SELFHOST_OUT = $editorOut
RunEditor $project @("-executeMethod", "UnitySelfHostEntry.Run", "-quit", "-selfHostScenario", "translate", "-selfHostOut", $editorOut) "editor-translate.log"
Set-Content (Join-Path $editorOut "process-exit.txt") "0"
RunEditor $project @("-executeMethod", "SelfHostBuild.Build", "-quit") "build.log"

$player = Join-Path $project "Build/SelfHost.exe"
Set-StackReserve $player
$playerOut = Join-Path $WorkDirectory "player-translate"
$playerRepeat = Join-Path $WorkDirectory "player-translate-repeat"
RunSelfHost $player "translate" $playerOut "player-translate.log"
RunSelfHost $player "translate" $playerRepeat "player-translate-repeat.log"
$missingOut = Join-Path $WorkDirectory "missing-wasm2cs"
$corruptOut = Join-Path $WorkDirectory "corrupt-wasm2cs"
$env:SH10_SCENARIO = "translate-missing-wasm2cs"
$env:WASM2CS_SELFHOST_OUT = $missingOut
RunEditor $project @("-executeMethod", "UnitySelfHostEntry.Run", "-quit", "-selfHostScenario", "translate-missing-wasm2cs", "-selfHostOut", $missingOut) "editor-missing.log"
Set-Content (Join-Path $missingOut "process-exit.txt") "0"
$env:SH10_SCENARIO = "translate-corrupt-wasm2cs"
$env:WASM2CS_SELFHOST_OUT = $corruptOut
RunEditor $project @("-executeMethod", "UnitySelfHostEntry.Run", "-quit", "-selfHostScenario", "translate-corrupt-wasm2cs", "-selfHostOut", $corruptOut) "editor-corrupt.log"
Set-Content (Join-Path $corruptOut "process-exit.txt") "0"
RunSelfHost $player "translate-missing-wasm2cs" (Join-Path $WorkDirectory "player-missing") "player-missing.log"
RunSelfHost $player "translate-corrupt-wasm2cs" (Join-Path $WorkDirectory "player-corrupt") "player-corrupt.log"

$il2cppOut = Join-Path $WorkDirectory "il2cpp-sources"
Invoke-SelfHosting @("unity-check", "--player", $playerOut, "--editor", $editorOut, "--repeat", $playerRepeat, "--missing", (Join-Path $WorkDirectory "player-missing"), "--corrupt", (Join-Path $WorkDirectory "player-corrupt"), "--editor-missing", $missingOut, "--editor-corrupt", $corruptOut, "--il2cpp-out", $il2cppOut)

RunEditor $generatedProject @("-createProject", "`"$generatedProject`"", "-quit") "generated-create.log"
InstallPackage $generatedProject "generated-install.log"
$module = Join-Path $generatedProject "Assets/GeneratedModule"
New-Item -ItemType Directory -Path $module, (Join-Path $generatedProject "Assets/Editor") -Force | Out-Null
Copy-Item -Path (Join-Path $il2cppOut "*.g.cs") -Destination $module
Get-ChildItem $module -Filter *.g.cs | ForEach-Object { Rename-Item $_.FullName ($_.Name -replace '\.g\.cs$', '.cs') }
Copy-Item (Join-Path $repo "unity/SelfHost/GeneratedModule.asmdef") (Join-Path $module "Wasm2Cs.GeneratedModule.asmdef")
Copy-Item (Join-Path $repo "unity/SelfHost/GeneratedModuleRunner.cs") (Join-Path $module "GeneratedModuleRunner.cs")
Copy-Item (Join-Path $repo "unity/SelfHost/Editor/GeneratedModuleBuild.cs") (Join-Path $generatedProject "Assets/Editor/GeneratedModuleBuild.cs")
RunEditor $generatedProject @("-executeMethod", "GeneratedModuleBuild.Verify", "-quit") "generated-editor.log"
RunEditor $generatedProject @("-executeMethod", "GeneratedModuleBuild.Build", "-quit") "generated-build.log"
$generatedLog = Join-Path $WorkDirectory "generated-player.log"
$generatedPlayer = Start-Process (Join-Path $generatedProject "Build/Generated.exe") -ArgumentList @("-batchmode", "-nographics", "-logFile", "`"$generatedLog`"") -PassThru
if (!$generatedPlayer.WaitForExit($timeoutMs)) { $generatedPlayer.Kill(); throw "Obtained-source player timed out" }
if ($generatedPlayer.ExitCode -ne 0 -or !(Select-String -Path $generatedLog -Pattern "WASM2CS_GENERATED_PASS" -Quiet)) {
    Get-Content $generatedLog -Tail 80
    throw "Obtained-source IL2CPP player failed"
}
Write-Output "PASS: Unity Editor and Windows x64 IL2CPP self-hosting. Logs: $WorkDirectory"
