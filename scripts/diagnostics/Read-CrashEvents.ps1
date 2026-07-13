$events = Get-WinEvent -FilterHashtable @{ LogName='Application'; StartTime=(Get-Date).AddHours(-3) } -ErrorAction SilentlyContinue |
  Where-Object { $_.ProviderName -in @('Application Error','.NET Runtime','Windows Error Reporting') -and $_.Message -match 'ReVM' } |
  Select-Object -First 12 TimeCreated,ProviderName,Id,Message

if (-not $events) {
  'No ReVM crash events found in Application log in the last 3 hours.'
  exit 0
}

$events | Format-List
