param([ValidateSet('2024','2025','2026')][string]$RevitVersion='2025')
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$cfg="Release R$($RevitVersion.Substring(2))"
$package=Join-Path $root "src/Sinotech.Revit.Addin/bin/AddIn $RevitVersion $cfg/revit_mcp_plugin"
$server=Join-Path $root 'server'
if(!(Test-Path -LiteralPath (Join-Path $package 'RevitMCPPlugin.dll'))){throw "Build Sinotech.Revit.Addin ($cfg) first."}
if(!(Test-Path -LiteralPath (Join-Path $package "Commands/RevitMCPCommandSet/$RevitVersion/RevitMCPCommandSet.dll"))){throw "Build Sinotech.Revit.Commands ($cfg) first."}
if(!(Test-Path -LiteralPath (Join-Path $package "Commands/SinotechExtensions/$RevitVersion/SinotechExtensions.dll"))){throw "Build Sinotech.Revit.Extensions ($cfg) first."}
if(!(Test-Path -LiteralPath (Join-Path $server 'build/chat/index.js'))){throw 'Run npm ci and npm run build in server first.'}
$payload=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot "payloads/$RevitVersion"))
$allowed=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'payloads'))+[IO.Path]::DirectorySeparatorChar
if (!$payload.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)) {throw 'Unsafe payload path'}
if (Test-Path -LiteralPath $payload) {
 if((Get-Item -LiteralPath $payload).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Payload cannot be a junction'}
 Remove-Item -LiteralPath $payload -Recurse -Force
}
New-Item -ItemType Directory -Path $payload -Force | Out-Null
# Per-user state (Logs, commandRegistry.json) is created at runtime and must not be shipped.
Get-ChildItem -LiteralPath $package -Force | Where-Object { $_.Name -ne 'Logs' -and $_.Extension -ne '.pdb' } | Copy-Item -Destination $payload -Recurse -Force
Get-ChildItem -LiteralPath $payload -Recurse -File | Where-Object { $_.Extension -eq '.pdb' -or $_.Name -eq 'commandRegistry.json' } | Remove-Item -Force
$runtime=Join-Path $payload 'runtime'
New-Item -ItemType Directory -Path $runtime -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $server 'build') -Destination $runtime -Recurse -Force
Copy-Item -LiteralPath (Join-Path $server 'node_modules') -Destination $runtime -Recurse -Force
foreach($name in 'package.json','package-lock.json'){Copy-Item -LiteralPath (Join-Path $server $name) -Destination $runtime -Force}
Push-Location $runtime
# npm writes warnings to stderr; keep them out of PowerShell's error stream and rely on the exit code.
try{& cmd.exe /c 'npm prune --omit=dev --no-audit --no-fund >nul 2>&1';if($LASTEXITCODE -ne 0){throw 'npm prune failed'}}finally{Pop-Location}
$nodeDirectory=Join-Path $runtime 'node'
New-Item -ItemType Directory -Path $nodeDirectory -Force | Out-Null
$nodePath=(Get-Command node.exe -ErrorAction Stop).Source
$nodeVersion=(& $nodePath -p 'process.versions.node').Trim()
if($nodeVersion -notmatch '^\d+\.\d+\.\d+$' -or [version]$nodeVersion -lt [version]'20.0.0'){throw 'Node 20 or later is required'}
if((& $nodePath -p 'process.arch').Trim() -ne 'x64'){throw 'An x64 Node runtime is required'}
Copy-Item -LiteralPath $nodePath -Destination (Join-Path $nodeDirectory 'node.exe')
# better-sqlite3 is a native module: it must load with the bundled node.exe.
& (Join-Path $nodeDirectory 'node.exe') -e "const D=require(process.argv[1]);new D(':memory:').close()" (Join-Path $runtime 'node_modules/better-sqlite3')
if($LASTEXITCODE -ne 0){throw 'better-sqlite3 does not load with the bundled Node runtime'}
$license=Join-Path $PSScriptRoot "generated/node-LICENSE-$nodeVersion.txt"
if(!(Test-Path -LiteralPath $license)){
 New-Item -ItemType Directory -Path (Split-Path $license) -Force | Out-Null
 $tybim='C:/Prj/Revit/TYBIM_MCP/artifacts/node-runtime-LICENSE-' + $nodeVersion + '.txt'
 if(Test-Path -LiteralPath $tybim){Copy-Item -LiteralPath $tybim -Destination $license}
 else {Invoke-WebRequest "https://raw.githubusercontent.com/nodejs/node/v$nodeVersion/LICENSE" -OutFile $license}
}
Copy-Item -LiteralPath $license -Destination (Join-Path $nodeDirectory 'LICENSE.txt')
foreach($name in 'LICENSE','NOTICE.md'){Copy-Item -LiteralPath (Join-Path $root $name) -Destination $payload -Force}
@{nodeExecutable='runtime/node/node.exe';runtimeScript='runtime/build/chat/index.js';codexExecutable='';stateDirectory="%LOCALAPPDATA%\Sinotech\McpChat\$RevitVersion"} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $payload 'chat.config.json') -Encoding utf8
& node (Join-Path $PSScriptRoot 'Generate-Setup.mjs') $RevitVersion
if($LASTEXITCODE -ne 0){throw 'Setup generation failed'}
