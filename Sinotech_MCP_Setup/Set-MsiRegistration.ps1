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
[void]$database.Commit()
[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($database)
[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer)
Write-Host "MSI registration enabled: Sinotech_MCP for Revit $RevitVersion ($version)"
