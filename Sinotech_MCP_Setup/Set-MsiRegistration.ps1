param([ValidateSet('2024','2025','2026')][string]$RevitVersion='2025')
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$version=(Get-Content (Join-Path $root 'server/package.json') -Raw | ConvertFrom-Json).version
$msi=Join-Path $PSScriptRoot "Release/Sinotech_MCP_Revit${RevitVersion}_$version.msi"
$installer=New-Object -ComObject WindowsInstaller.Installer
$database=$installer.OpenDatabase($msi,1)
function Sql([string]$query){$view=$database.OpenView($query);try{[void]$view.Execute()}finally{[void]$view.Close();[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($view)}}
# VSI can emit the registration component/custom action while dropping its
# custom root directory (observed on the newly generated 2026 setup project).
$directoryView=$database.OpenView('SELECT `Directory` FROM `Directory` WHERE `Directory`=''SINOTECHREGISTERDIR''')
[void]$directoryView.Execute();$directoryRecord=$directoryView.Fetch()
[void]$directoryView.Close();[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($directoryView)
if($null -eq $directoryRecord){
 Sql 'INSERT INTO `Directory` (`Directory`,`Directory_Parent`,`DefaultDir`) VALUES (''SINOTECHREGISTERDIR'',''TARGETDIR'',''.'')'
}
# Use standard MSI registration. No parallel hand-written Uninstall registry key.
foreach($name in @('ARPSYSTEMCOMPONENT','ARPNOREMOVE','ALLUSERS')){
 Sql "DELETE FROM ``Property`` WHERE ``Property``='$name'"
}
foreach($entry in @{ARPNOMODIFY='1';ARPNOREPAIR='1';MSIINSTALLPERUSER='1'}.GetEnumerator()){
 Sql "DELETE FROM ``Property`` WHERE ``Property``='$($entry.Key)'"
 Sql "INSERT INTO ``Property`` (``Property``,``Value``) VALUES ('$($entry.Key)','$($entry.Value)')"
}
# 完整解除安裝時清除執行期間產生的檔案（Logs、commandRegistry.json、revit-data.db 等）。
# AI助手登入資料（%LOCALAPPDATA%\Sinotech\McpChat\<年份>）刻意保留，重新安裝後不需再登入；要清除請在面板按「登出／清除金鑰」。
# MSI 只會移除自己安裝的檔案，其餘檔案會讓資料夾殘留。
# 升級時（UPGRADINGPRODUCTCODE）舊版也會被「解除安裝」，此時不可執行，否則會刪掉新版檔案與使用者設定。
# Clean runtime-generated files on full uninstall only; never during the old product's removal in an upgrade.
$directoryView=$database.OpenView('SELECT `Directory` FROM `Directory` WHERE `Directory`=''SystemFolder''')
[void]$directoryView.Execute();$directoryRecord=$directoryView.Fetch()
[void]$directoryView.Close();[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($directoryView)
if($null -eq $directoryRecord){
 Sql 'INSERT INTO `Directory` (`Directory`,`Directory_Parent`,`DefaultDir`) VALUES (''SystemFolder'',''TARGETDIR'',''.'')'
}
$cleanup=[ordered]@{
 SINOTECH_CLEAN_APP="""[SystemFolder]cmd.exe"" /d /c rmdir /s /q ""[TARGETDIR]$RevitVersion"" & rmdir ""[TARGETDIR]"""
}
$sequence=3550 # RemoveFiles (3500) 之後、RemoveFolders (3600) 之前
foreach($action in $cleanup.GetEnumerator()){
 Sql "DELETE FROM ``CustomAction`` WHERE ``Action``='$($action.Key)'"
 Sql "DELETE FROM ``InstallExecuteSequence`` WHERE ``Action``='$($action.Key)'"
 # 1122 = 34（在指定目錄執行 exe）+ 64（忽略結束代碼）+ 1024（deferred）
 Sql "INSERT INTO ``CustomAction`` (``Action``,``Type``,``Source``,``Target``) VALUES ('$($action.Key)',1122,'SystemFolder','$($action.Value)')"
 Sql "INSERT INTO ``InstallExecuteSequence`` (``Action``,``Condition``,``Sequence``) VALUES ('$($action.Key)','REMOVE~=""ALL"" AND NOT UPGRADINGPRODUCTCODE AND TARGETDIR<>""""',$sequence)"
 $sequence++
}
[void]$database.Commit()
[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($database)
[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer)
Write-Host "MSI registration enabled: Sinotech_MCP for Revit $RevitVersion ($version)"
