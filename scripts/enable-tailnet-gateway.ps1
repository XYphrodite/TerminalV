#Requires -Version 5.1
#Requires -RunAsAdministrator
[CmdletBinding()]
param([ValidateRange(1, 65535)][int]$Port = 5454)

$ErrorActionPreference = 'Stop'
$tailscale = Join-Path $env:ProgramFiles 'Tailscale\tailscale.exe'
if (!(Test-Path -LiteralPath $tailscale)) { throw 'Install and sign in to Tailscale on this PC first.' }
$addresses = @(& $tailscale ip) | Where-Object { $_ -match '^(100\.|fd7a:115c:a1e0:)' }
if ($LASTEXITCODE -ne 0 -or $addresses.Count -eq 0) { throw 'This PC has no Tailscale address.' }
$url = "http://*:$Port/"
$existing = & netsh.exe http show urlacl "url=$url"
if ($LASTEXITCODE -ne 0) {
    $account = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    & netsh.exe http add urlacl "url=$url" "user=$account"
    if ($LASTEXITCODE -ne 0) { throw 'Cannot reserve the gateway URL.' }
}
$ruleName = "TerminalV-Tailnet-$Port"
if (!(Get-NetFirewallRule -Name $ruleName -ErrorAction SilentlyContinue)) {
    New-NetFirewallRule -Name $ruleName -DisplayName "TerminalV mobile over Tailscale ($Port)" -Direction Inbound -Action Allow -Protocol TCP -LocalPort $Port -LocalAddress $addresses -RemoteAddress '100.64.0.0/10','fd7a:115c:a1e0::/48' -Profile Any | Out-Null
}
Write-Output 'Done. Restart TerminalV and enable its gateway in Settings. Phone access still requires confirmation on this PC.'
