#Requires -Version 5.1
# Load only the installer's shortcut functions, never its download/install/PATH code.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$installer = Join-Path (Split-Path -Parent $PSScriptRoot) 'install.ps1'
$source = Get-Content -LiteralPath $installer -Raw -Encoding UTF8
if ($source.ToCharArray() | Where-Object { [int]$_ -gt 127 }) { throw 'Installer must remain ASCII-only' }
$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseInput($source, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw ($parseErrors | Out-String) }
foreach ($name in @('New-TerminalVShortcut', 'Install-TerminalVShortcuts')) {
    $function = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
    . ([scriptblock]::Create($function.Extent.Text))
}
function Write-Step { param($Message) }
function Send-ShortcutNotification { param($Path, $Created) $script:notices.Add(@($Path, $Created)) }
function Assert { param([bool] $Condition, [string] $Message) if (-not $Condition) { throw $Message } }
function Edit-Link {
    param([string] $Path, [scriptblock] $Action)
    $shell = New-Object -ComObject WScript.Shell
    $link = $null
    try { $link = $shell.CreateShortcut($Path); & $Action $link }
    finally {
        if ($null -ne $link) { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($link) | Out-Null }
        [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null
    }
}
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('terminalv-installer-shortcuts-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$passed = 0
try {
    foreach ($choice in @(@($false, $false), @($false, $true), @($true, $false), @($true, $true))) {
        $case = Join-Path $testRoot ([guid]::NewGuid().ToString('N'))
        [IO.Directory]::CreateDirectory($case) | Out-Null
        # Unicode, spaces, punctuation and redirected folders; no executable is ever launched.
        $appDir = Join-Path $case ("ZIP & user's [" + [char]0x0416 + ']')
        [IO.Directory]::CreateDirectory($appDir) | Out-Null
        $exe = Join-Path $appDir 'TerminalV.exe'
        [IO.File]::WriteAllText($exe, 'Test sentinel')
        $folders = @{ Programs = (Join-Path $case 'redirected\Programs'); DesktopDirectory = (Join-Path $case 'OneDrive\Desktop') }
        $resolveFolder = { param($name) $folders[$name] }
        $script:notices = New-Object 'System.Collections.Generic.List[object]'
        $warnings = @()
        Install-TerminalVShortcuts -Executable $exe -NoShortcut:$choice[0] -DesktopShortcut:$choice[1] -FolderPath $resolveFolder -WarningVariable warnings
        if ($warnings.Count) {
            $Error[0] | Format-List * -Force | Out-Host
            throw "Unexpected shortcut warning: $($warnings -join '; ')"
        }
        $menu = Join-Path $folders.Programs 'TerminalV.lnk'
        $desktop = Join-Path $folders.DesktopDirectory 'TerminalV.lnk'
        Assert ((Test-Path -LiteralPath $menu) -eq (-not $choice[0])) 'Wrong Start Menu default'
        Assert ((Test-Path -LiteralPath $desktop) -eq (-not $choice[0] -and $choice[1])) 'Wrong desktop opt-in/NoShortcut priority'
        foreach ($path in @($menu, $desktop)) {
            if (Test-Path -LiteralPath $path) {
                Edit-Link $path { param($link)
                    Assert ($link.TargetPath -eq $exe) 'Wrong target'
                    Assert ($link.WorkingDirectory -eq $appDir) 'Wrong working directory'
                    Assert ($link.IconLocation -eq "$exe,0") 'Wrong icon'
                    Assert ($link.Arguments -eq '') 'Unexpected arguments'
                }
            }
        }
        Assert ($script:notices.Count -eq @((Get-ChildItem -LiteralPath $case -Filter '*.lnk' -Recurse)).Count) 'Missing notifications'
        $passed++
        Write-Host "PASS installer flags NoShortcut=$($choice[0]) DesktopShortcut=$($choice[1])"
    }
    # Use the last case (NoShortcut) for repeat/conflict/failure checks.
    $null = New-TerminalVShortcut -Executable $exe -Folder $folders.Programs
    Edit-Link $menu { param($link) $link.Description = 'User description'; $link.WorkingDirectory = $testRoot; $link.Save() }
    $null = New-TerminalVShortcut -Executable $exe -Folder $folders.Programs
    Edit-Link $menu { param($link)
        Assert ($link.Description -eq 'User description' -and $link.WorkingDirectory -eq $testRoot) 'User customization lost'
    }
    Assert (@(Get-ChildItem -LiteralPath $folders.Programs -Force).Count -eq 1) 'Duplicate or temporary link left behind'
    Assert ($script:notices[$script:notices.Count - 1][1] -eq $false) 'Refresh notification missing'
    $passed++
    Write-Host 'PASS installer idempotent refresh preserves customizations'

    foreach ($argumentConflict in @($true, $false)) {
        Edit-Link $menu { param($link)
            if ($argumentConflict) { $link.Arguments = '--help' }
            else { $link.Arguments = ''; $link.TargetPath = (Join-Path $testRoot 'another.exe') }
            $link.Save()
        }
        $before = (Get-FileHash -LiteralPath $menu).Hash
        $refused = $false
        try { $null = New-TerminalVShortcut -Executable $exe -Folder $folders.Programs }
        catch { $refused = $true }
        Assert $refused 'Conflicting shortcut accepted'
        Assert ((Get-FileHash -LiteralPath $menu).Hash -eq $before) 'Conflicting shortcut modified'
        Assert (@(Get-ChildItem -LiteralPath $folders.Programs -Force).Count -eq 1) 'Temporary link leaked after failure'
        $passed++
        Write-Host "PASS installer preserves conflict (arguments=$argumentConflict)"
    }
    $warnings = @()
    Install-TerminalVShortcuts -Executable $exe -DesktopShortcut -FolderPath $resolveFolder -WarningVariable warnings -WarningAction SilentlyContinue
    Assert ($warnings.Count -eq 1 -and (Test-Path -LiteralPath $desktop)) 'One failure must not prevent the other destination'
    $passed++
    Write-Host 'PASS installer partial failure is reported and does not abort installation'
    Write-Host "Installer shortcut checks: $passed passed. No real user shortcuts, PATH or installed files touched."
} finally {
    $resolved = [IO.Path]::GetFullPath($testRoot)
    $tempParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($tempParent, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolved) -notmatch '^terminalv-installer-shortcuts-[a-f0-9]{32}$') { throw 'Unsafe cleanup path' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
