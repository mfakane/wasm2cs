param(
    [string]$Editor = "C:\Program Files\Unity\Hub\Editor\6000.6.0f1\Editor\Unity.exe",
    [string]$WorkDirectory = (Join-Path $env:TEMP ("wasm2cs-unity-" + [guid]::NewGuid().ToString("N")))
)
$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent
$project = Join-Path $WorkDirectory "Smoke"
$package = Join-Path $WorkDirectory "Package"
function RunEditor([string[]]$Arguments, [string]$LogName) {
    Write-Output "Unity: $LogName"
    $log = Join-Path $WorkDirectory $LogName
    $all = @("-batchmode", "-nographics", "-projectPath", "`"$project`"", "-logFile", "`"$log`"") + $Arguments
    $process = Start-Process -FilePath $Editor -ArgumentList $all -PassThru
    if (!$process.WaitForExit(1200000)) { $process.Kill(); throw "Unity timed out; see $log" }
    if ($process.ExitCode -ne 0) { Get-Content $log -Tail 100; throw "Unity exited $($process.ExitCode); see $log" }
}
New-Item -ItemType Directory -Path $WorkDirectory -Force | Out-Null
Write-Output "Unity verification artifacts: $WorkDirectory"
Copy-Item (Join-Path $repo "unity/Packages/com.mfakane.wasm2cs") $package -Recurse
Copy-Item (Join-Path $repo "src/Wasm2Cs.Generator/bin/Debug/netstandard2.0/Wasm2Cs.Generator.dll") (Join-Path $package "Runtime/Wasm2Cs.Generator.dll")
RunEditor @("-createProject", "`"$project`"", "-quit") "create.log"
New-Item -ItemType Directory -Path (Join-Path $project "Assets/Editor") -Force | Out-Null
Copy-Item (Join-Path $repo "unity/Bootstrap/PackageInstaller.cs") (Join-Path $project "Assets/Editor/PackageInstaller.cs")
$env:WASM2CS_UNITY_PACKAGE = $package.Replace('\', '/')
RunEditor @("-executeMethod", "PackageInstaller.Install") "install.log"
Copy-Item (Join-Path $repo "samples/Smoke/Arithmetic.wasm") (Join-Path $project "Assets/Arithmetic.wasm")
RunEditor @("-executeMethod", "Wasm2Cs.Editor.WasmAssetBridge.Synchronize", "-quit") "inputs.log"
Copy-Item (Join-Path $repo "unity/Smoke/Editor/BridgeVerify.cs") (Join-Path $project "Assets/Editor/BridgeVerify.cs")
RunEditor @("-executeMethod", "BridgeVerify.Present", "-quit") "bridge-present.log"
New-Item -ItemType Directory -Path (Join-Path $project "Assets/Moved") -Force | Out-Null
Move-Item (Join-Path $project "Assets/Arithmetic.wasm") (Join-Path $project "Assets/Moved/Arithmetic.wasm")
RunEditor @("-executeMethod", "BridgeVerify.Present", "-quit") "bridge-moved.log"
$wasmPath = Join-Path $project "Assets/Moved/Arithmetic.wasm"
$bytes = [IO.File]::ReadAllBytes($wasmPath)
$changed = $false
for ($i = 0; $i -lt $bytes.Length - 2; $i++) {
    if ($bytes[$i] -eq 0x41 -and $bytes[$i + 1] -eq 0x7f -and $bytes[$i + 2] -eq 0x0b) {
        $bytes[$i + 1] = 0x7e; $changed = $true; break
    }
}
if (!$changed) { throw "Fixture no longer contains the expected negative constant" }
[IO.File]::WriteAllBytes($wasmPath, $bytes)
RunEditor @("-executeMethod", "Wasm2Cs.Editor.WasmAssetBridge.Synchronize", "-quit") "bridge-update.log"
RunEditor @("-executeMethod", "BridgeVerify.Changed", "-quit") "bridge-changed.log"
Remove-Item $wasmPath
RunEditor @("-executeMethod", "Wasm2Cs.Editor.WasmAssetBridge.Synchronize", "-quit") "bridge-remove.log"
RunEditor @("-executeMethod", "BridgeVerify.Missing", "-quit") "bridge-missing.log"
Copy-Item (Join-Path $repo "samples/Smoke/Arithmetic.wasm") (Join-Path $project "Assets/Arithmetic.wasm")
RunEditor @("-executeMethod", "Wasm2Cs.Editor.WasmAssetBridge.Synchronize", "-quit") "bridge-restore.log"
Copy-Item (Join-Path $repo "unity/Smoke/SmokeRunner.cs") (Join-Path $project "Assets/SmokeRunner.cs")
Copy-Item (Join-Path $repo "unity/Smoke/Editor/SmokeBuild.cs") (Join-Path $project "Assets/Editor/SmokeBuild.cs")
RunEditor @("-executeMethod", "SmokeBuild.Verify", "-quit") "editor-test.log"
RunEditor @("-executeMethod", "SmokeBuild.Build", "-quit") "build.log"
$playerLog = Join-Path $WorkDirectory "player.log"
$player = Start-Process (Join-Path $project "Build/Smoke.exe") -ArgumentList @("-batchmode", "-nographics", "-logFile", "`"$playerLog`"") -PassThru
if (!$player.WaitForExit(120000)) { $player.Kill(); throw "Player timed out" }
if ($player.ExitCode -ne 0 -or !(Select-String -Path $playerLog -Pattern "WASM2CS_SMOKE_PASS" -Quiet)) {
    Get-Content $playerLog; throw "IL2CPP player failed"
}
Write-Output "PASS: Unity Editor and Windows x64 IL2CPP execution. Logs: $WorkDirectory"
