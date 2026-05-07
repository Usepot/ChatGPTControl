Write-Output '--- visible windows ---'
Get-Process | Where-Object { $_.MainWindowTitle } | Sort-Object ProcessName | ForEach-Object {
  [pscustomobject]@{ Process = $_.ProcessName; Title = $_.MainWindowTitle }
} | Format-Table -Wrap -AutoSize

Write-Output '--- desktop screenshot artifact already saved at ---'
Write-Output 'desktop_state\desktop_now.png'
