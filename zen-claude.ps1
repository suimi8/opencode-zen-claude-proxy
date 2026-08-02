$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$started = $false
try {
  Invoke-RestMethod "http://127.0.0.1:4050/health" -TimeoutSec 2 | Out-Null
} catch {
  $started = $true
}
$p = $null
if ($started) {
  Get-Content "$root\.env.local" | ForEach-Object {
    if ($_ -match "^([^=]+)=(.*)$" -and $matches[1] -ne "") {
      Set-Item "env:$($matches[1])" -Value $matches[2]
    }
  }
  $node = (Get-Command node).Source
  $p = Start-Process $node -ArgumentList "$root\zen-proxy-launch.js" -WorkingDirectory $root -PassThru
  Start-Sleep -Milliseconds 1500
}
try {
  & claude @args
  if ($LASTEXITCODE -ne $null) { exit $LASTEXITCODE }
} finally {
  if ($p) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
}
