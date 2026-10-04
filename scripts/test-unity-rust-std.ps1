param(
    [string]$Editor = "C:\Program Files\Unity\Hub\Editor\6000.6.0f1\Editor\Unity.exe",
    [Parameter(Mandatory = $true)][string]$PackagePath,
    [string]$OraclePath = "",
    [string]$WorkDirectory = (Join-Path $env:TEMP ("wasm2cs-rust-std-" + [guid]::NewGuid().ToString("N")))
)
$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent
if (!$OraclePath) { $OraclePath = Join-Path $repo "artifacts/t02-rust-std/unity-oracle.json" }
$oracle = Get-Content -LiteralPath $OraclePath -Raw | ConvertFrom-Json
$fixture = Join-Path $repo "samples/RustStd/RustStd190.wasm"
$fixtureHash = (Get-FileHash -LiteralPath $fixture -Algorithm SHA256).Hash.ToLowerInvariant()
if ($fixtureHash -ne $oracle.fixtureSha256) { throw "Oracle does not match the input WASM." }
if ($oracle.cases.Count -ne 18 -or $oracle.rows.Count -ne 18) { throw "Expected all 18 reference cases." }
$project = Join-Path $WorkDirectory "RustStd"
if (Test-Path -LiteralPath $project) { throw "Use a fresh WorkDirectory; $project already exists." }
function RunEditor([string[]]$Arguments, [string]$LogName) {
    Write-Output "Unity: $LogName"
    $log = Join-Path $WorkDirectory $LogName
    $all = @("-batchmode", "-nographics", "-projectPath", "`"$project`"", "-logFile", "`"$log`"") + $Arguments
    $process = Start-Process -FilePath $Editor -ArgumentList $all -PassThru
    if (!$process.WaitForExit(1200000)) { $process.Kill(); throw "Unity timed out; see $log" }
    if ($process.ExitCode -ne 0) { Get-Content $log -Tail 100; throw "Unity exited $($process.ExitCode); see $log" }
}
New-Item -ItemType Directory -Path $WorkDirectory -Force | Out-Null
Write-Output "Rust std Unity artifacts: $WorkDirectory"
$package = Join-Path $WorkDirectory "com.mfakane.wasm2cs.tgz"
Copy-Item -LiteralPath $PackagePath -Destination $package
@{
    fixtureSha256 = $fixtureHash
    packageSha256 = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant()
    oracleSha256 = (Get-FileHash -LiteralPath $OraclePath -Algorithm SHA256).Hash.ToLowerInvariant()
    nodeVersion = $oracle.nodeVersion
    editor = $Editor
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $WorkDirectory "inputs.json") -Encoding UTF8
RunEditor @("-createProject", "`"$project`"", "-quit") "create.log"
New-Item -ItemType Directory -Path (Join-Path $project "Assets/Editor"), (Join-Path $project "Assets/Resources") -Force | Out-Null
Copy-Item (Join-Path $repo "unity/Bootstrap/PackageInstaller.cs") (Join-Path $project "Assets/Editor/PackageInstaller.cs")
$env:WASM2CS_UNITY_PACKAGE = $package.Replace('\', '/')
RunEditor @("-executeMethod", "PackageInstaller.Install") "install.log"
$manifest = Get-Content (Join-Path $project "Packages/manifest.json") -Raw | ConvertFrom-Json
if (!$manifest.dependencies.'com.mfakane.wasm2cs') { throw "UPM package was not installed." }
Copy-Item -LiteralPath $fixture -Destination (Join-Path $project "Assets/RustStd190.wasm")
RunEditor @("-executeMethod", "Wasm2Cs.Editor.WasmAssetBridge.Synchronize", "-quit") "inputs.log"
Copy-Item (Join-Path $repo "unity/RustStd/RustStdRunner.cs") (Join-Path $project "Assets/RustStdRunner.cs")
Copy-Item (Join-Path $repo "unity/RustStd/Editor/RustStdBuild.cs") (Join-Path $project "Assets/Editor/RustStdBuild.cs")
Copy-Item -LiteralPath $OraclePath -Destination (Join-Path $project "Assets/Resources/RustStdOracle.json")
$env:WASM2CS_RUST_STD_OUTPUT = Join-Path $WorkDirectory "editor.json"
RunEditor @("-executeMethod", "RustStdBuild.Verify", "-quit") "editor-test.log"
$editorResult = Get-Content $env:WASM2CS_RUST_STD_OUTPUT -Raw | ConvertFrom-Json
if (!$editorResult.passed -or $editorResult.backend -ne "Editor Mono") { throw "Editor did not report success." }
RunEditor @("-executeMethod", "RustStdBuild.Build", "-quit") "build.log"
$env:WASM2CS_RUST_STD_OUTPUT = Join-Path $WorkDirectory "player.json"
$playerLog = Join-Path $WorkDirectory "player.log"
$player = Start-Process (Join-Path $project "Build/RustStd.exe") -ArgumentList @("-batchmode", "-nographics", "-logFile", "`"$playerLog`"") -PassThru
if (!$player.WaitForExit(120000)) { $player.Kill(); throw "Player timed out; see $playerLog" }
if ($player.ExitCode -ne 0 -or !(Select-String -Path $playerLog -Pattern "WASM2CS_RUST_STD_PASS" -Quiet)) {
    Get-Content $playerLog; throw "IL2CPP player failed."
}
$playerResult = Get-Content $env:WASM2CS_RUST_STD_OUTPUT -Raw | ConvertFrom-Json
if (!$playerResult.passed -or $playerResult.backend -ne "IL2CPP" -or $playerResult.rows.Count -ne 18) {
    throw "Player did not report all cases on IL2CPP."
}
Write-Output "PASS: Rust std Unity Editor and Windows x64 IL2CPP; 18 calls and complete memory match Node. Logs: $WorkDirectory"
