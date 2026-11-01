<#
  CWY:LS Mod Loader - installer for Windows. Double-click install.bat (it runs this file).
  Unattended (testing, automation):
    powershell -NoProfile -ExecutionPolicy Bypass -File install.ps1 -GameRoot "<game folder>" -Action Install|Uninstall|UninstallAll|Trace|Status [-Trace On|Full|Off]

  Nothing is ever deleted: uninstalling moves files into a dated folder inside the game folder.
  Leaving takes two presses: Ctrl+C (read as a key, never a signal) or q on the main menu, twice within
  3 seconds. Installing and uninstalling are never interrupted.
  The animated screens need a console window of at least 60 x 23 (the classic console is asked for
  100 x 32; Windows Terminal keeps its own size). A smaller window, redirected input or output, or a
  console without VT support get a plain numbered menu with the same actions.

  Test hooks (environment variables):
    CWY_GAME             the game folder (skips the Steam search)
    CWY_ROWS, CWY_COLS   force the animated screens at this size, even without a console
    CWY_KEYS             scripted keys instead of the keyboard, comma separated: up, down, left, right,
                         enter, esc, q, ctrlc, a (any other key), 1-7, text:<line> (one typed line, for
                         the game-folder prompt), sleep:<ms> (time passes, no key). When the list runs
                         out, Ctrl+C is sent (twice leaves).
    CWY_THEME            light | dark      CWY_COLORS   16 for the 16-colour fallback
  Windows PowerShell 5.1 compatible. Saved as UTF-8 with BOM: 5.1 reads BOM-less files as ANSI.
#>
param(
    [string]$GameRoot = '',
    [ValidateSet('', 'Install', 'Uninstall', 'UninstallAll', 'Trace', 'Status')]
    [string]$Action = '',
    [ValidateSet('', 'On', 'Full', 'Off')]
    [string]$Trace = ''
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$VERSION         = '__VERSION__'
$VERSION_UI      = 'v' + ($VERSION -split '-', 2)[0]
$FILES           = $PSScriptRoot
$BEPINEX_VERSION = '5.4.23.6'
$BEPINEX_ZIP     = [IO.Path]::Combine($FILES, "BepInEx_win_x64_$BEPINEX_VERSION.zip")
$GAME_FOLDER     = 'Chill with You Lo-Fi Story'
$EXE             = 'Chill With You.exe'
$PROCESS_NAME    = 'Chill With You'
$PLUGIN_DIR_NAME = 'CwyLsModLoader'
$MODS_DIR_NAME   = 'CWYLS Mods'
$TRACE_CFG_NAME  = 'cwyls.modloader.trace.cfg'
$UTF8            = New-Object System.Text.UTF8Encoding($false)
$SUMMARY         = New-Object System.Collections.Generic.List[string]
$script:ERR = ''; $script:MOVED_TO = ''; $script:MIGRATED = 0; $script:UNBLOCKED = $false

function Stamp { return (Get-Date).ToString('yyyyMMdd-HHmmss') }
function Game-Path([string]$rel) { return [IO.Path]::Combine($script:GAME_ROOT, $rel) }
function Test-Exists([string]$p) { return ([IO.File]::Exists($p) -or [IO.Directory]::Exists($p)) }
function IDiv([int]$a, [int]$b) { return [int][Math]::Truncate($a / $b) }
function Log-Line([string]$t) {
    try {
        if ($script:GAME_ROOT -and [IO.Directory]::Exists((Game-Path 'BepInEx'))) {
            [IO.File]::AppendAllText((Game-Path 'BepInEx\cwyls-installer.log'), ((Get-Date).ToString('yyyy-MM-dd HH:mm:ss') + '  ' + $t + "`r`n"), $UTF8)
        }
    } catch { }
}

# ================================================================ the game
function Steam-Libraries {
    $libs = New-Object System.Collections.Generic.List[string]
    foreach ($key in @('HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam', 'HKLM:\SOFTWARE\Valve\Steam')) {
        try {
            $p = Get-ItemProperty -Path $key -ErrorAction Stop
            foreach ($v in @($p.SteamPath, $p.InstallPath)) { if ($v) { $libs.Add(($v -replace '/', '\')) } }
        } catch { }
    }
    foreach ($steam in @($libs)) {
        try {
            $vdf = [IO.Path]::Combine($steam, 'steamapps\libraryfolders.vdf')
            if ([IO.File]::Exists($vdf)) {
                foreach ($m in [regex]::Matches([IO.File]::ReadAllText($vdf), '"path"\s+"([^"]+)"')) {
                    $libs.Add(($m.Groups[1].Value -replace '\\\\', '\'))
                }
            }
        } catch { }
    }
    foreach ($d in @('C:\Program Files (x86)\Steam', 'D:\SteamLibrary', 'E:\SteamLibrary')) { $libs.Add($d) }
    return ($libs | Select-Object -Unique)
}
function Find-Game {
    foreach ($lib in @(Steam-Libraries)) {
        try {
            $candidate = [IO.Path]::Combine($lib, "steamapps\common\$GAME_FOLDER")
            if ([IO.File]::Exists([IO.Path]::Combine($candidate, $EXE))) { return $candidate }
        } catch { }
    }
    return ''
}
function Clean-Path([string]$p) {  # a dragged-in or pasted path: spaces, quotes and a trailing \ removed
    if ($null -eq $p) { return '' }
    $p = $p.Trim()
    if ($p.StartsWith("'")) { $p = $p.Substring(1) }
    if ($p.EndsWith("'")) { $p = $p.Substring(0, $p.Length - 1) }
    if ($p.StartsWith('"')) { $p = $p.Substring(1) }
    if ($p.EndsWith('"')) { $p = $p.Substring(0, $p.Length - 1) }
    $p = $p.Trim()
    if ($p.Length -gt 3) { $p = $p.TrimEnd('\', '/') }
    if ($p -and -not [IO.Path]::IsPathRooted($p)) {
        try { $p = [IO.Path]::GetFullPath([IO.Path]::Combine((Get-Location).ProviderPath, $p)) } catch { }
    }
    return $p
}
function Is-Game([string]$p) {
    if (-not $p) { return $false }
    try { return [IO.File]::Exists([IO.Path]::Combine($p, $EXE)) } catch { return $false }
}
function Is-Demo([string]$p) {
    try { $n = [IO.Path]::GetFileName($p.TrimEnd('\', '/')) } catch { $n = $p }
    return ($n -match '(^|[^A-Za-z])demo([^A-Za-z]|$)')
}
function Test-GameRunning { return (@(Get-Process -Name $PROCESS_NAME -ErrorAction SilentlyContinue).Count -gt 0) }

# ================================================================ what is installed
function Installed-Version {
    $f = Game-Path "BepInEx\plugins\$PLUGIN_DIR_NAME\VERSION.txt"
    try {
        if ([IO.File]::Exists($f)) { $l = @([IO.File]::ReadAllLines($f)); if ($l.Count -gt 0) { return ($l[0] -replace '\s', '') } }
    } catch { }
    return ''
}
function BepInEx-Installed { return ([IO.File]::Exists((Game-Path 'BepInEx\core\BepInEx.dll')) -and [IO.File]::Exists((Game-Path 'winhttp.dll'))) }
function Loader-Installed { return [IO.Directory]::Exists((Game-Path "BepInEx\plugins\$PLUGIN_DIR_NAME")) }
function Trace-Level {
    $cfg = Game-Path "BepInEx\config\$TRACE_CFG_NAME"; $level = ''
    try {
        if ([IO.File]::Exists($cfg)) {
            foreach ($l in [IO.File]::ReadAllLines($cfg)) { if ($l -match '^\s*Level\s*=\s*([A-Za-z]*)') { $level = $Matches[1]; break } }
        }
    } catch { }
    if ($level -ceq 'Events') { return 'on' }
    if ($level -ceq 'Trace') { return 'full' }
    return 'off'
}
function Mods-Count {
    $d = Game-Path $MODS_DIR_NAME; $n = 0
    try {
        if ([IO.Directory]::Exists($d)) {
            foreach ($sub in [IO.Directory]::GetDirectories($d)) { if ([IO.File]::Exists([IO.Path]::Combine($sub, 'manifest.json'))) { $n++ } }
        }
    } catch { }
    return $n
}
function Other-Mods {
    $d = Game-Path 'BepInEx\plugins'; $names = @()
    try {
        if ([IO.Directory]::Exists($d)) {
            foreach ($entry in [IO.Directory]::GetFileSystemEntries($d)) {
                $n = [IO.Path]::GetFileName($entry)
                if ($n -eq $PLUGIN_DIR_NAME -or $n.StartsWith('.')) { continue }
                $names += $n
            }
        }
    } catch { }
    return (@($names | Sort-Object) -join ', ')
}
function Old-Configs {  # com.<author>.cwy.patcher*.cfg and com.<author>.cwyls.modloader*.cfg from builds before the rename
    $c = Game-Path 'BepInEx\config'
    if (-not [IO.Directory]::Exists($c)) { return }
    foreach ($f in [IO.Directory]::GetFiles($c, 'com.*.cfg')) {
        if ([IO.Path]::GetFileName($f) -match '^com\..+?\.(cwy\.patcher|cwyls\.modloader)(.*)$') { $f }
    }
}
function Needs-Migration {
    if ([IO.Directory]::Exists((Game-Path 'BepInEx\plugins\CWY'))) { return $true }
    if ([IO.Directory]::Exists((Game-Path 'CWYPacks')) -and -not (Test-Exists (Game-Path $MODS_DIR_NAME))) { return $true }
    if (@(Old-Configs).Count -gt 0) { return $true }
    return $false
}
function Loader-Dlls {
    $d = [IO.Path]::Combine($FILES, $PLUGIN_DIR_NAME)
    if ([IO.Directory]::Exists($d)) { [IO.Directory]::GetFiles($d, '*.dll') }
}

# ================================================================ the work (each sets ERR and returns $false on failure)
function Unblock-Package {  # files from a downloaded zip carry the "from the internet" mark; clear it before copying
    if ($script:UNBLOCKED) { return }
    $script:UNBLOCKED = $true
    try { Get-ChildItem -LiteralPath $FILES -Recurse -File -ErrorAction SilentlyContinue | Unblock-File -ErrorAction SilentlyContinue } catch { }
}
function Copy-Tree([string]$src, [string]$dst) {
    [void][IO.Directory]::CreateDirectory($dst)
    foreach ($d in [IO.Directory]::GetDirectories($src, '*', 'AllDirectories')) { [void][IO.Directory]::CreateDirectory($dst + $d.Substring($src.Length)) }
    foreach ($f in [IO.Directory]::GetFiles($src, '*', 'AllDirectories')) { [IO.File]::Copy($f, $dst + $f.Substring($src.Length), $true) }
}
function Expand-Zip([string]$zip, [string]$dest) {
    try {
        Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction Stop
        [IO.Compression.ZipFile]::ExtractToDirectory($zip, $dest)
    } catch {
        Expand-Archive -LiteralPath $zip -DestinationPath $dest -Force
    }
}
function Do-Nothing { return $true }
function Do-Migrate {  # builds from before the rename; nothing is deleted
    $old = Game-Path ('BepInEx\_old\' + (Stamp))
    $script:MIGRATED = 0
    $cwy = Game-Path 'BepInEx\plugins\CWY'
    if ([IO.Directory]::Exists($cwy)) {
        try {
            [void][IO.Directory]::CreateDirectory($old)
            [IO.Directory]::Move($cwy, [IO.Path]::Combine($old, 'plugins-CWY'))
        } catch { $script:ERR = 'Could not move the earlier build out of BepInEx\plugins\CWY.'; return $false }
        $script:MIGRATED = 1
    }
    foreach ($cfg in @(Old-Configs)) {
        $name = [IO.Path]::GetFileName($cfg)
        $null = $name -match '^com\..+?\.(cwy\.patcher|cwyls\.modloader)(.*)$'
        $new = Game-Path ('BepInEx\config\cwyls.modloader' + $Matches[2])
        try {
            if (-not [IO.File]::Exists($new)) { [IO.File]::Copy($cfg, $new) }
            [void][IO.Directory]::CreateDirectory([IO.Path]::Combine($old, 'config'))
            [IO.File]::Move($cfg, [IO.Path]::Combine($old, "config\$name"))
        } catch { }
        $script:MIGRATED = 1
    }
    $packs = Game-Path 'CWYPacks'
    if ([IO.Directory]::Exists($packs) -and -not (Test-Exists (Game-Path $MODS_DIR_NAME))) {
        try { [IO.Directory]::Move($packs, (Game-Path $MODS_DIR_NAME)) } catch { $script:ERR = "Could not rename CWYPacks to `"$MODS_DIR_NAME`"."; return $false }
        $script:MIGRATED = 1
    }
    return $true
}
function Do-BepInEx {
    if (-not [IO.File]::Exists($BEPINEX_ZIP)) {
        $script:ERR = "BepInEx is missing from this download (files\$([IO.Path]::GetFileName($BEPINEX_ZIP))). Download the release zip again."; return $false
    }
    Unblock-Package
    $tmp = [IO.Path]::Combine([IO.Path]::GetTempPath(), 'cwyls-bepinex-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    try {
        try { [void][IO.Directory]::CreateDirectory($tmp) } catch { $script:ERR = 'Could not create a temporary folder.'; return $false }
        try { Expand-Zip $BEPINEX_ZIP $tmp } catch { $script:ERR = 'Could not unpack BepInEx.'; return $false }
        try {
            Copy-Tree ([IO.Path]::Combine($tmp, 'BepInEx')) (Game-Path 'BepInEx')
            # Everything but BepInEx's changelog.txt, which would sit next to the game's own files.
            foreach ($f in @('winhttp.dll', 'doorstop_config.ini', '.doorstop_version')) {
                $src = [IO.Path]::Combine($tmp, $f)
                if ([IO.File]::Exists($src)) { [IO.File]::Copy($src, (Game-Path $f), $true) }
            }
        } catch { $script:ERR = 'Could not copy BepInEx into the game folder.'; return $false }
        try {
            $log = [IO.Path]::Combine($tmp, 'changelog.txt')
            if ([IO.File]::Exists($log)) { [IO.File]::Copy($log, (Game-Path 'BepInEx\bepinex-changelog.txt'), $true) }
            foreach ($d in @('plugins', 'patchers', 'config')) { [void][IO.Directory]::CreateDirectory((Game-Path "BepInEx\$d")) }
        } catch { }
        return $true
    } finally {
        # only the folder unpacked above, in the temp folder
        try {
            if ([IO.Path]::GetFileName($tmp).StartsWith('cwyls-bepinex-') -and [IO.Directory]::Exists($tmp)) { [IO.Directory]::Delete($tmp, $true) }
        } catch { }
    }
}
function Do-Loader {
    $dlls = @(Loader-Dlls)
    if ($dlls.Count -eq 0) { $script:ERR = "The mod loader is missing from this download (files\$PLUGIN_DIR_NAME). Download the release zip again."; return $false }
    Unblock-Package
    try {
        $dest = Game-Path "BepInEx\plugins\$PLUGIN_DIR_NAME"
        [void][IO.Directory]::CreateDirectory($dest)
        foreach ($dll in $dlls) { [IO.File]::Copy($dll, [IO.Path]::Combine($dest, [IO.Path]::GetFileName($dll)), $true) }
        [IO.File]::WriteAllText([IO.Path]::Combine($dest, 'VERSION.txt'), "$VERSION`r`n", $UTF8)
    } catch { $script:ERR = 'Could not copy the mod loader into BepInEx\plugins.'; return $false }
    return $true
}
function Do-ModsFolder {
    try {
        $d = Game-Path $MODS_DIR_NAME
        [void][IO.Directory]::CreateDirectory($d)
        $readme = [IO.Path]::Combine($d, 'README.txt')
        if (-not [IO.File]::Exists($readme)) {
            [IO.File]::WriteAllText($readme, ("Put mods for CWY:LS Mod Loader here, one folder per mod (each with a manifest.json).`r`n" +
                "The game reads this folder when it starts.`r`n"), $UTF8)
        }
    } catch { $script:ERR = "Could not create the `"$MODS_DIR_NAME`" folder."; return $false }
    return $true
}
function Do-UninstallLoader {
    $name = 'uninstalled-' + (Stamp)
    $dest = Game-Path "BepInEx\_old\$name"
    try {
        [void][IO.Directory]::CreateDirectory($dest)
        [IO.Directory]::Move((Game-Path "BepInEx\plugins\$PLUGIN_DIR_NAME"), [IO.Path]::Combine($dest, $PLUGIN_DIR_NAME))
    } catch { $script:ERR = 'Could not move the mod loader out of BepInEx\plugins.'; return $false }
    $config = Game-Path 'BepInEx\config'
    if ([IO.Directory]::Exists($config)) {
        foreach ($cfg in [IO.Directory]::GetFiles($config, 'cwyls.modloader*.cfg')) {
            try {
                [void][IO.Directory]::CreateDirectory([IO.Path]::Combine($dest, 'config'))
                [IO.File]::Move($cfg, [IO.Path]::Combine($dest, 'config\' + [IO.Path]::GetFileName($cfg)))
            } catch { }
        }
    }
    $script:MOVED_TO = "BepInEx\_old\$name"
    return $true
}
function Do-UninstallAll {
    $name = '_CWYLS-uninstalled-' + (Stamp)
    $dest = Game-Path $name
    try { [void][IO.Directory]::CreateDirectory($dest) } catch { $script:ERR = "Could not create $name in the game folder."; return $false }
    foreach ($f in @('BepInEx', 'winhttp.dll', 'doorstop_config.ini', '.doorstop_version')) {
        $p = Game-Path $f
        try {
            if ([IO.Directory]::Exists($p)) { [IO.Directory]::Move($p, [IO.Path]::Combine($dest, $f)) }
            elseif ([IO.File]::Exists($p)) { [IO.File]::Move($p, [IO.Path]::Combine($dest, $f)) }
        } catch { $script:ERR = "Could not move $f out of the game folder."; return $false }
    }
    $script:MOVED_TO = $name
    return $true
}
function Set-Trace([string]$want) {  # off | on | full
    if ($want -ceq 'on') { $level = 'Events' }
    elseif ($want -ceq 'full') { $level = 'Trace' }
    elseif ($want -ceq 'off') { $level = 'Off' }
    else { $script:ERR = 'trace must be on, full or off'; return $false }
    $configDir = Game-Path 'BepInEx\config'
    try { [void][IO.Directory]::CreateDirectory($configDir) } catch { $script:ERR = 'Could not create BepInEx\config.'; return $false }
    $cfg = [IO.Path]::Combine($configDir, $TRACE_CFG_NAME)
    try {
        if ([IO.File]::Exists($cfg)) {
            $lines = @([IO.File]::ReadAllLines($cfg))
            $out = New-Object System.Collections.Generic.List[string]
            if (@($lines | Where-Object { $_ -match '^\s*Level\s*=' }).Count -gt 0) {
                foreach ($l in $lines) { if ($l -match '^\s*Level\s*=') { $out.Add("Level = $level") } else { $out.Add($l) } }
            } elseif (@($lines | Where-Object { $_ -match '^\[Trace\]' }).Count -gt 0) {
                foreach ($l in $lines) { $out.Add($l); if ($l -match '^\[Trace\]') { $out.Add("Level = $level") } }
            } else {
                foreach ($l in $lines) { $out.Add($l) }
                foreach ($l in @('', '[Trace]', '', "Level = $level")) { $out.Add($l) }
            }
            [IO.File]::WriteAllLines($cfg, $out.ToArray(), $UTF8)
        } else {
            [IO.File]::WriteAllLines($cfg, [string[]]@('[Trace]', '', "Level = $level"), $UTF8)
        }
    } catch { $script:ERR = "Could not write BepInEx\config\$TRACE_CFG_NAME."; return $false }
    return $true
}
function Full-Install {  # the whole install, in order; used by the plain and unattended modes
    if (-not (Do-Migrate)) { return $false }
    if (-not (Do-BepInEx)) { return $false }
    if (-not (Do-Loader)) { return $false }
    return (Do-ModsFolder)
}

# ================================================================ unattended mode (flags) and the plain menu
function Say([string]$t) { [Console]::Out.WriteLine($t) }
function Ask([string]$t) { [Console]::Out.Write($t) }
function Plain-Status {
    Say "  Game:        $($script:GAME_ROOT)"
    $iv = Installed-Version; if (-not $iv) { $iv = 'not installed' }
    Say "  Mod loader:  $iv"
    if (BepInEx-Installed) { Say '  BepInEx:     installed' } else { Say '  BepInEx:     not installed' }
    Say "  Trace:       $(Trace-Level)"
    Say "  Mods:        $(Mods-Count) in `"$MODS_DIR_NAME`""
}
function Plain-CheckGame {
    if (-not (Is-Game $script:GAME_ROOT)) { Say "ERROR: `"$($script:GAME_ROOT)`" does not contain `"$EXE`"."; return $false }
    if (Is-Demo $script:GAME_ROOT) { Say 'ERROR: This looks like the free demo. CWY:LS Mod Loader only works with the full game on Steam.'; return $false }
    return $true
}
function Plain-Install {
    if (Test-GameRunning) { Say 'The game is running. Quit it first.'; return $false }
    if (Full-Install) {
        Log-Line "installed $VERSION (plain)"
        Say "Installed CWY:LS Mod Loader $VERSION_UI and BepInEx $BEPINEX_VERSION. Your save files were not touched."
        $others = Other-Mods
        if ($others) { Say "Note: other BepInEx mods are installed ($others). CWY:LS Mod Loader is standalone and not tested with them." }
        Say 'Start the game from Steam as usual. ctrl shift C opens the console.'
        return $true
    }
    Say "ERROR: $($script:ERR) (nothing was deleted)"; return $false
}
function Plain-Uninstall {
    if (Test-GameRunning) { Say 'The game is running. Quit it first.'; return $false }
    if (-not (Loader-Installed)) { Say 'The mod loader is not installed.'; return $true }
    if (Do-UninstallLoader) {
        Log-Line "uninstalled the loader (plain) -> $($script:MOVED_TO)"
        Say "Removed. Moved to $($script:MOVED_TO); delete it whenever you like. BepInEx, your mods and your save backups stay."; return $true
    }
    Say "ERROR: $($script:ERR)"; return $false
}
function Plain-UninstallAll {
    if (Test-GameRunning) { Say 'The game is running. Quit it first.'; return $false }
    if (Do-UninstallAll) { Say "BepInEx and the mod loader moved to $($script:MOVED_TO). Your mods, saves and save backups stay."; return $true }
    Say "ERROR: $($script:ERR)"; return $false
}
$CTRLC_LINE = [string][char]3
$TYPEAHEAD = New-Object 'System.Collections.Generic.Queue[System.ConsoleKeyInfo]'
$script:PLAIN_KEYS = $false; $script:LAST_CTRLC = -100000
function Is-CtrlC($k) { return ([int]$k.KeyChar -eq 3 -or ($k.Key -eq [ConsoleKey]::C -and ($k.Modifiers -band [ConsoleModifiers]::Control))) }
function Read-Line([bool]$ignoreCtrlC = $false) {  # one typed line; $CTRLC_LINE for Ctrl+C; $null at the end of input
    if ($Scripted) {
        while ($true) {
            if ($KEYQ.Count -eq 0) { return $null }
            $t = $KEYQ.Dequeue()
            if ($t -eq 'ctrlc') { if ($ignoreCtrlC) { continue }; Say ''; return $CTRLC_LINE }
            if ($t -match '^sleep:(\d+)$') { Start-Sleep -Milliseconds ([int]$Matches[1]); continue }
            if ($t.StartsWith('text:')) { return $t.Substring(5) }
            if ($t -eq 'enter') { return '' }
            return $t
        }
    }
    if (-not $script:PLAIN_KEYS) { return [Console]::In.ReadLine() }
    # Console input, key by key: Ctrl+C arrives as a key (TreatControlCAsInput) and is handled here.
    $buf = New-Object System.Text.StringBuilder
    while ($true) {
        if ($TYPEAHEAD.Count -gt 0) { $k = $TYPEAHEAD.Dequeue() } else { $k = [Console]::ReadKey($true) }
        if (Is-CtrlC $k) { if ($ignoreCtrlC) { continue }; Say ''; return $CTRLC_LINE }
        if ($k.Key -eq [ConsoleKey]::Enter) { Say ''; return $buf.ToString() }
        if ($k.Key -eq [ConsoleKey]::Backspace) { if ($buf.Length -gt 0) { [void]$buf.Remove($buf.Length - 1, 1); Ask "`b `b" }; continue }
        if ([int]$k.KeyChar -ge 32) { [void]$buf.Append($k.KeyChar); Ask ([string]$k.KeyChar) }
    }
}
function Plain-CtrlC {  # Ctrl+C in the plain menu: the first press only warns; a second within 3 seconds quits
    if ($CLOCK.ElapsedMilliseconds - $script:LAST_CTRLC -le 3000) { exit 130 }
    $script:LAST_CTRLC = $CLOCK.ElapsedMilliseconds
    Say '  (Press Ctrl+C again to quit.)'
}
function Drop-CtrlC {  # after an action: keys typed meanwhile stay, Ctrl+C presses from that time are dropped
    if (-not $script:PLAIN_KEYS) { return }
    while ([Console]::KeyAvailable) { $k = [Console]::ReadKey($true); if (-not (Is-CtrlC $k)) { $TYPEAHEAD.Enqueue($k) } }
}
function Plain-Menu {
    Say ''; Say "  CWY:LS  M O D   L O A D E R   $VERSION_UI"; Say '  for Chill With You: Lo-Fi Story  -  unofficial fan project'; Say ''
    if (-not $script:GAME_ROOT) {
        Say 'I could not find the game in your Steam libraries.'
        while ($true) {
            Ask "Drag the game folder (the one with `"$EXE`") here, then press Enter: "
            $line = Read-Line
            if ($line -eq $CTRLC_LINE) { Plain-CtrlC; continue }
            if ($null -eq $line) { exit 1 }
            break
        }
        $script:GAME_ROOT = Clean-Path $line
    }
    if (-not (Plain-CheckGame)) { exit 1 }
    while ($true) {
        Say ''; Plain-Status; Say ''
        Say '  [1] Install or update      [2] Diagnostic trace       [3] Open the mods folder'
        Say '  [4] Uninstall the loader   [5] Uninstall everything   [Q] Quit'
        Ask "`r`n  Choose: "
        $c = Read-Line
        if ($c -eq $CTRLC_LINE) { Plain-CtrlC; continue }
        if ($null -eq $c) { exit 0 }
        $c = $c.Trim()
        # The work itself cannot be interrupted: Ctrl+C is ignored until the action is done.
        if ($c -eq '1') { [void](Plain-Install) }
        elseif ($c -eq '2') {
            Ask '  Trace [on/full/off]: '
            $t = Read-Line $true; if ($null -eq $t) { $t = '' }
            $t = $t.Trim().ToLower()
            if (Set-Trace $t) { Say "  Trace: $t (from the next game start)." } else { Say "  $($script:ERR)" }
        }
        elseif ($c -eq '3') {
            $modsDir = Game-Path $MODS_DIR_NAME
            try { [void][IO.Directory]::CreateDirectory($modsDir) } catch { }
            if ($RealConsole) {
                try { Invoke-Item -LiteralPath $modsDir } catch { }
                Say "  Opened `"$MODS_DIR_NAME`"."
            } else { Say "  Your mods folder: $modsDir" }
        }
        elseif ($c -eq '4') { [void](Plain-Uninstall) }
        elseif ($c -eq '5') {
            Ask '  Move BepInEx and the mod loader out of the game folder? [y/N] '
            $yn = Read-Line $true
            if ($yn -cmatch '^[yY]$') { [void](Plain-UninstallAll) } else { Say '  Nothing changed.' }
        }
        elseif ($c -eq 'q') { exit 0 }
        Drop-CtrlC
    }
}

# ================================================================ the console (VT mode, keys, theme)
$CON_SRC = @'
using System;
using System.Runtime.InteropServices;
using System.Text;
namespace CwyLsInstaller {
    public static class Con {
        [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr GetStdHandle(int nStdHandle);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
        [DllImport("kernel32.dll")] public static extern IntPtr GetConsoleWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    }
}
'@
$script:OUT_MODE_SAVED = $null; $script:H_OUT = [IntPtr]::Zero
function Enable-VT {  # ENABLE_VIRTUAL_TERMINAL_PROCESSING on the output handle; $false when there is no console
    try {
        if (-not ('CwyLsInstaller.Con' -as [type])) { Add-Type -TypeDefinition $CON_SRC -ErrorAction Stop }
        $h = [CwyLsInstaller.Con]::GetStdHandle(-11)
        $m = [uint32]0
        if (-not [CwyLsInstaller.Con]::GetConsoleMode($h, [ref]$m)) { return $false }
        if (($m -band 4) -eq 0) {
            if (-not [CwyLsInstaller.Con]::SetConsoleMode($h, [uint32]($m -bor 4))) { return $false }
            $script:OUT_MODE_SAVED = $m; $script:H_OUT = $h
        }
        return $true
    } catch { return $false }
}
function Console-Class {  # "PseudoConsoleWindow" under Windows Terminal (also when it is the default terminal)
    try {
        $hw = [CwyLsInstaller.Con]::GetConsoleWindow()
        if ($hw -eq [IntPtr]::Zero) { return '' }
        $name = New-Object System.Text.StringBuilder 256
        [void][CwyLsInstaller.Con]::GetClassName($hw, $name, 256)
        return $name.ToString()
    } catch { return '' }
}
function Query-Theme {  # ask the terminal for its background colour (OSC 11); '' when it does not answer
    $resp = ''; $okIn = $false; $hIn = [IntPtr]::Zero; $inMode = [uint32]0
    try {
        $hIn = [CwyLsInstaller.Con]::GetStdHandle(-10)
        $okIn = [CwyLsInstaller.Con]::GetConsoleMode($hIn, [ref]$inMode)
        if ($okIn) { [void][CwyLsInstaller.Con]::SetConsoleMode($hIn, [uint32]($inMode -bor 0x0200)) }
        [Console]::Out.Write("$ESC]11;?$ESC\")
        $sw = [Diagnostics.Stopwatch]::StartNew()
        while ($sw.ElapsedMilliseconds -lt 250) {
            if ([Console]::KeyAvailable) {
                $resp += [string][Console]::ReadKey($true).KeyChar
                if ($resp -match 'rgb:[0-9a-fA-F/]+(\x07|\x1b\\)') { break }
            } else { Start-Sleep -Milliseconds 10 }
        }
    } catch { } finally {
        if ($okIn) { try { [void][CwyLsInstaller.Con]::SetConsoleMode($hIn, $inMode) } catch { } }
    }
    if ($resp -match 'rgb:([0-9a-fA-F]{2})[0-9a-fA-F]*/([0-9a-fA-F]{2})[0-9a-fA-F]*/([0-9a-fA-F]{2})') {
        $lum = [Math]::Floor((299 * [Convert]::ToInt32($Matches[1], 16) + 587 * [Convert]::ToInt32($Matches[2], 16) + 114 * [Convert]::ToInt32($Matches[3], 16)) / 1000)
        if ($lum -ge 128) { return 'light' }
        return 'dark'
    }
    return ''
}

$ESC = [string][char]27; $RST = "$ESC[0m"; $BOLD = "$ESC[1m"
$SB = New-Object System.Text.StringBuilder 65536
$script:FAST = $false; $script:KEY = ''
function Put([string]$s) { [void]$SB.Append($s) }
function At([int]$row, [int]$col) { [void]$SB.Append("$ESC[$row;${col}H") }
function Flush-Screen { if ($SB.Length -gt 0) { [Console]::Out.Write($SB.ToString()); [void]$SB.Clear() } }
function Nap([int]$ms) { Flush-Screen; if ($ms -gt 0) { Start-Sleep -Milliseconds $ms } }
function Poll-Key {  # non-blocking; $true with KEY set when a key was waiting
    $script:KEY = ''
    if ($Scripted) {
        if ($KEYQ.Count -eq 0) { $script:KEY = 'ctrlc'; return $true }      # out of keys: Ctrl+C (twice leaves)
        $t = $KEYQ.Dequeue()
        if ($t -match '^sleep:(\d+)$') { Nap ([int]$Matches[1]); return $false }
        if ($t -match '^(up|down|left|right|enter|esc|q|ctrlc|[1-7])$') { $script:KEY = $t } else { $script:KEY = 'other' }
        return $true
    }
    if (-not [Console]::KeyAvailable) { return $false }
    $k = [Console]::ReadKey($true)
    if (Is-CtrlC $k) { $script:KEY = 'ctrlc'; return $true }
    $name = [string]$k.Key
    if ($name -eq 'UpArrow') { $script:KEY = 'up' }
    elseif ($name -eq 'DownArrow') { $script:KEY = 'down' }
    elseif ($name -eq 'RightArrow') { $script:KEY = 'right' }
    elseif ($name -eq 'LeftArrow') { $script:KEY = 'left' }
    elseif ($name -eq 'Enter') { $script:KEY = 'enter' }
    elseif ($name -eq 'Escape') { $script:KEY = 'esc' }
    elseif ($k.KeyChar -eq [char]'q' -or $k.KeyChar -eq [char]'Q') { $script:KEY = 'q' }
    elseif ($k.KeyChar -ge [char]'1' -and $k.KeyChar -le [char]'7') { $script:KEY = [string]$k.KeyChar }
    else { $script:KEY = 'other' }
    return $true
}
function Frame([int]$ms) {  # one frame: wait, and any key turns the rest of the current animation instant
    if ($script:FAST) { return }
    Nap $ms
    if (Poll-Key) { if ($script:KEY -eq 'q' -or $script:KEY -eq 'ctrlc') { Quit-Key } else { $script:FAST = $true } }
}

# ================================================================ main
$IN_UI = $false; $FATAL = $null; $ENC_SAVED = $null; $RESIZED = $null; $EXIT_CODE = 0; $script:CTRLC_SAVED = $null
$CLOCK = [Diagnostics.Stopwatch]::StartNew()
$script:GAME_ROOT = Clean-Path $GameRoot
if (-not $script:GAME_ROOT -and $env:CWY_GAME) { $script:GAME_ROOT = Clean-Path $env:CWY_GAME }
$Scripted = [bool]$env:CWY_KEYS
$KEYQ = New-Object System.Collections.Generic.Queue[string]
if ($Scripted) { foreach ($t in ($env:CWY_KEYS -split ',')) { $t = $t.Trim(); if ($t) { $KEYQ.Enqueue($t) } } }
try { $ENC_SAVED = [Console]::OutputEncoding; [Console]::OutputEncoding = $UTF8 } catch { $ENC_SAVED = $null }

try {
    if ($Action) {
        if (-not $script:GAME_ROOT) { $script:GAME_ROOT = Find-Game }
        if (-not $script:GAME_ROOT) { Say 'ERROR: game not found; pass -GameRoot'; exit 1 }
        if (-not (Plain-CheckGame)) { exit 1 }
        if ($Action -eq 'Install') { if (Plain-Install) { exit 0 } else { exit 1 } }
        if ($Action -eq 'Uninstall') { if (Plain-Uninstall) { exit 0 } else { exit 1 } }
        if ($Action -eq 'UninstallAll') { if (Plain-UninstallAll) { exit 0 } else { exit 1 } }
        if ($Action -eq 'Trace') {
            $want = $Trace.ToLower()
            if (Set-Trace $want) { Log-Line "trace $want (flag)"; Say "Diagnostic trace: $want (from the next game start)."; exit 0 }
            Say "ERROR: $($script:ERR)"; exit 2
        }
        if ($Action -eq 'Status') { Plain-Status; exit 0 }
    }

    if (-not $script:GAME_ROOT) { $script:GAME_ROOT = Find-Game }

    # Ctrl+C is read as a key from here on, never a signal: nothing can stop a copy or a move halfway,
    # and cmd (install.bat) never sees it. Leaving takes two presses (see Quit-Key / Plain-CtrlC).
    $inRedir0 = $true; try { $inRedir0 = [Console]::IsInputRedirected } catch { }
    if (-not $Scripted -and -not $inRedir0) {
        try { $script:CTRLC_SAVED = [Console]::TreatControlCAsInput; [Console]::TreatControlCAsInput = $true; $script:PLAIN_KEYS = $true } catch { }
    }

    # ------------------------------------------------------------ console, size and capabilities
    $FIRST_RUN = $true; if ($script:GAME_ROOT -and (Loader-Installed)) { $FIRST_RUN = $false }
    $forced = ($env:CWY_ROWS -match '^\d+$') -and ($env:CWY_COLS -match '^\d+$')
    $inRedir = $true; $outRedir = $true
    try { $inRedir = [Console]::IsInputRedirected; $outRedir = [Console]::IsOutputRedirected } catch { }
    $RealConsole = (-not $Scripted) -and (-not $inRedir) -and (-not $outRedir)
    $VT_OK = $false
    if ($RealConsole -or $forced) { $VT_OK = Enable-VT }
    $MODERN = [bool]$env:WT_SESSION
    if (-not $MODERN -and $RealConsole -and $VT_OK -and (Console-Class) -eq 'PseudoConsoleWindow') { $MODERN = $true }
    if ($forced) { $ROWS = [int]$env:CWY_ROWS; $COLS = [int]$env:CWY_COLS }
    else { try { $ROWS = [Console]::WindowHeight; $COLS = [Console]::WindowWidth } catch { $ROWS = 24; $COLS = 80 } }
    # The classic console can be made bigger (Windows Terminal keeps its own size); put back on exit.
    if (-not $forced -and $RealConsole -and $VT_OK -and -not $MODERN -and ($ROWS -lt 30 -or $COLS -lt 84)) {
        try {
            if ([Console]::BufferWidth -ge 100 -and [Console]::BufferHeight -ge 32 -and [Console]::LargestWindowWidth -ge 100 -and [Console]::LargestWindowHeight -ge 32) {
                $RESIZED = @($COLS, $ROWS)
                [Console]::SetWindowSize(100, 32)
                Start-Sleep -Milliseconds 100
                $ROWS = [Console]::WindowHeight; $COLS = [Console]::WindowWidth
            }
        } catch { }
    }
    $MODE = 'full'
    if (-not $RealConsole -and -not $forced) { $MODE = 'plain' }
    elseif ($env:TERM -eq 'dumb') { $MODE = 'plain' }
    elseif (-not $VT_OK -and -not $forced) { $MODE = 'plain' }
    elseif ($ROWS -ge 30 -and $COLS -ge 84) { $MODE = 'full' }
    elseif ($ROWS -ge 23 -and $COLS -ge 60) { $MODE = 'compact' }
    else { $MODE = 'plain' }

    # Light or dark background? CWY_THEME first, then the terminal's own answer (OSC 11, Windows
    # Terminal), then COLORFGBG. Unknown means dark.
    $THEME = $env:CWY_THEME
    if (-not $THEME -and $MODE -ne 'plain' -and $RealConsole -and $VT_OK -and $MODERN) { $THEME = Query-Theme }
    if (-not $THEME -and $env:COLORFGBG) {
        $bgc0 = ($env:COLORFGBG -split ';')[-1]
        if ($bgc0 -eq '7' -or $bgc0 -eq '15') { $THEME = 'light' } else { $THEME = 'dark' }
    }
    if (-not $THEME) { $THEME = 'dark' }

    $COLORS = 256; if ($env:CWY_COLORS -match '^\d+$') { $COLORS = [int]$env:CWY_COLORS }
    $CC = New-Object string[] 256; $BGC = New-Object string[] 256
    if ($COLORS -ge 256) {
        for ($n = 0; $n -lt 256; $n++) { $CC[$n] = "$ESC[38;5;${n}m"; $BGC[$n] = "$ESC[48;5;${n}m" }
    } else {
        # 16-colour fallback, chosen per palette entry so pills and text stay readable
        for ($n = 0; $n -lt 256; $n++) {
            if ($n -ge 211 -and $n -le 219 -and $n -ne 216) { $CC[$n] = "$ESC[95m"; $BGC[$n] = "$ESC[105m" }
            elseif ($n -eq 216 -or $n -eq 222) { $CC[$n] = "$ESC[93m"; $BGC[$n] = "$ESC[103m" }
            elseif ($n -eq 147 -or $n -eq 153 -or $n -eq 183) { $CC[$n] = "$ESC[94m"; $BGC[$n] = "$ESC[104m" }
            elseif ($n -eq 117) { $CC[$n] = "$ESC[96m"; $BGC[$n] = "$ESC[106m" }
            elseif ($n -eq 114 -or $n -eq 121) { $CC[$n] = "$ESC[92m"; $BGC[$n] = "$ESC[102m" }
            elseif ($n -ge 248) { $CC[$n] = "$ESC[97m"; $BGC[$n] = "$ESC[47m" }
            elseif ($n -ge 232) { $CC[$n] = "$ESC[90m"; $BGC[$n] = "$ESC[100m" }
            else { $CC[$n] = "$ESC[30m"; $BGC[$n] = "$ESC[40m" }
        }
    }
    if ($THEME -eq 'light') {
        if ($COLORS -ge 256) {
            # the grey ramp flips (light text becomes dark), and each pastel gets a deeper version for white
            for ($n = 232; $n -le 255; $n++) { $m = 487 - $n; $CC[$n] = "$ESC[38;5;${m}m"; $BGC[$n] = "$ESC[48;5;${m}m" }
            foreach ($pair in @('217:167', '218:168', '219:170', '183:97', '147:61', '153:67', '216:166', '215:166', '221:136', '222:136',
                                '121:29', '120:28', '122:30', '123:30', '159:31', '117:31', '116:31', '211:161', '212:162', '213:163', '114:28', '203:160', '231:235')) {
                $from = [int]$pair.Split(':')[0]; $to = [int]$pair.Split(':')[1]
                $CC[$from] = "$ESC[38;5;${to}m"; $BGC[$from] = "$ESC[48;5;${to}m"
            }
        } else {
            for ($n = 0; $n -lt 256; $n++) {
                if ($CC[$n].EndsWith('[97m')) { $CC[$n] = "$ESC[30m" }
                elseif ($CC[$n].EndsWith('[95m')) { $CC[$n] = "$ESC[35m" }
                elseif ($CC[$n].EndsWith('[94m')) { $CC[$n] = "$ESC[34m" }
                elseif ($CC[$n].EndsWith('[96m')) { $CC[$n] = "$ESC[36m" }
                elseif ($CC[$n].EndsWith('[92m')) { $CC[$n] = "$ESC[32m" }
                elseif ($CC[$n].EndsWith('[93m')) { $CC[$n] = "$ESC[33m" }
                if ($BGC[$n].EndsWith('[47m')) { $BGC[$n] = "$ESC[100m" } elseif ($BGC[$n].EndsWith('[100m')) { $BGC[$n] = "$ESC[47m" }
            }
        }
    }

    # Glyphs: Windows Terminal has them all; the classic console's Consolas has no check marks and no
    # braille, so those get stand-ins it does have.
    if ($MODERN) {
        $GL = @{ CHECK = '✓'; CROSS = '✗' }
        $SPINNER = @('⠋', '⠙', '⠹', '⠸', '⠼', '⠴', '⠦', '⠧', '⠇', '⠏')
    } else {
        $GL = @{ CHECK = '√'; CROSS = '×' }
        $SPINNER = @('·', '•', '●', '•')
    }

    # ------------------------------------------------------------ plain mode (tiny window, no console, redirected)
    if ($MODE -eq 'plain') { Plain-Menu; exit 0 }

    # ------------------------------------------------------------ full-screen setup
    $IN_UI = $true
    # alternate screen, no line wrap at the right edge, hidden cursor, clear
    [Console]::Out.Write("$ESC[?1049h$ESC[?7l$ESC[?25l$ESC[2J")
    $TTY_OUT = $RealConsole          # File Explorer is only opened from a real console
    $rng = New-Object System.Random

    function Clear-Row([int]$row) { At $row $BOXL; [void]$SB.Append($SPACES) }        # wordmark rows: keep the stars beside them
    function Clear-Line([int]$row) { At $row 1; [void]$SB.Append("$ESC[2K") }       # text rows: long lines and sliding cards
    function Fit([string]$t) {  # shortened to the screen width, with … in the middle when it is too long
        $max = $COLS - 4
        if ($t.Length -gt $max) { $half = IDiv ($max - 1) 2; $t = $t.Substring(0, $half) + '…' + $t.Substring($t.Length - ($max - 1 - $half)) }
        return $t
    }
    function Center([int]$row, [string]$t, [string]$color) {
        $t = Fit $t
        At $row ((IDiv ($COLS - $t.Length) 2) + 1); [void]$SB.Append($color + $t + $RST)
    }

    # ------------------------------------------------------------ layout
    $ART = @(
        ' ██████╗ ██╗    ██╗ ██╗   ██╗     ██╗      ███████╗',
        '██╔════╝ ██║    ██║ ╚██╗ ██╔╝ ██╗ ██║      ██╔════╝',
        '██║      ██║ █╗ ██║  ╚████╔╝  ╚═╝ ██║      ███████╗',
        '██║      ██║███╗██║   ╚██╔╝   ██╗ ██║      ╚════██║',
        '╚██████╗ ╚███╔███╔╝    ██║    ╚═╝ ███████╗ ███████║',
        ' ╚═════╝  ╚══╝╚══╝     ╚═╝        ╚══════╝ ╚══════╝'
    )
    $ARTW = $ART[0].Length
    $PAL = @(217, 218, 219, 183, 147, 153)
    $X0 = (IDiv ($COLS - $ARTW) 2) + 1; if ($X0 -lt 1) { $X0 = 1 }

    # Rows. The wordmark, horizon and caption sit on top. Under them, in turn: the big THANK YOU, the
    # features panel, and finally the setup panel with the menu panel.
    if ($MODE -eq 'full') {
        $BLOCK = 28; $Y0 = (IDiv ($ROWS - $BLOCK) 2) + 1; if ($Y0 -lt 2) { $Y0 = 2 }
        $HR = $Y0 + 7; $CAP1 = $HR + 1; $CAP2 = $HR + 2
        $MSG = $HR + 4; $TY = $MSG + 1; $FP = $MSG + 1
        $SP = $MSG; $MP = $SP + 7; $KEYS2 = $MP + 9
    } else {
        $BLOCK = 23; $Y0 = (IDiv ($ROWS - $BLOCK) 2) + 1; if ($Y0 -lt 1) { $Y0 = 1 }
        $HR = $Y0 + 6; $CAP1 = $HR + 1; $CAP2 = 0
        $MSG = $HR + 2; $TY = $MSG + 1; $FP = $MSG
        $SP = $MSG; $MP = $SP + 6; $KEYS2 = $MP + 8
    }
    $KEYS1 = $FP + 8
    $PW = 74; if ($PW -gt $COLS - 6) { $PW = $COLS - 6 }      # panel width
    $PX = (IDiv ($COLS - $PW) 2) + 1                         # panel left column
    $IW = $PW - 4                                            # inner width (border + one space each side)
    $BOXL = $X0 - 6; if ($BOXL -lt 1) { $BOXL = 1 }
    $BOXW = $ARTW + 12; if ($BOXL + $BOXW - 1 -gt $COLS) { $BOXW = $COLS - $BOXL + 1 }
    $SPACES = ' ' * $BOXW

    function Paint([string]$row, [int]$hl = -99) {  # one wordmark row; columns hl-3..hl get the shine
        $o = New-Object System.Text.StringBuilder 512
        $len = $row.Length; $n = $PAL.Count; $last = ''
        for ($i = 0; $i -lt $len; $i++) {
            $ch = $row[$i]
            if ($ch -eq ' ') { [void]$o.Append(' '); continue }
            if ($i -ge $hl - 3 -and $i -le $hl) { $col = $CC[231] }
            elseif ($ch -eq '█') { $col = $CC[$PAL[[int][Math]::Floor($i * $n / $len)]] }
            else { $col = $CC[238] }
            if ($col -ne $last) { [void]$o.Append($col); $last = $col }
            [void]$o.Append($ch)
        }
        [void]$o.Append($RST)
        return $o.ToString()
    }
    $PAINTED = @(); foreach ($line in $ART) { $PAINTED += (Paint $line) }

    # ------------------------------------------------------------ the starfield
    $STARLV = @(236, 238, 240, 243, 246, 250)
    $SXL = New-Object System.Collections.Generic.List[int]; $SYL = New-Object System.Collections.Generic.List[int]
    $NSTARS = IDiv ($ROWS * $COLS) 55
    for ($s = 0; $s -lt $NSTARS; $s++) {
        $x = $rng.Next($COLS) + 1; $y = $rng.Next($ROWS) + 1
        if ($y -ge $MSG -and $y -le $Y0 + $BLOCK - 1) { continue }                                    # none in the text rows
        if ($y -ge $Y0 - 1 -and $y -lt $MSG -and $x -ge $BOXL -and $x -lt $BOXL + $BOXW) { continue }  # none behind the wordmark
        $SXL.Add($x); $SYL.Add($y)
    }
    $SX = $SXL.ToArray(); $SY = $SYL.ToArray()
    function Star([int]$i, [int]$lv, [string]$gch) { At $SY[$i] $SX[$i]; [void]$SB.Append($CC[$STARLV[$lv]] + $gch + $RST) }
    function Twinkle {
        Hint-Tick
        if ($SX.Count -eq 0) { return }
        $gch = '·'; if ($rng.Next(9) -eq 0) { $gch = '+' }
        Star ($rng.Next($SX.Count)) ($rng.Next(6)) $gch
    }

    # ------------------------------------------------------------ panels, pills and keycaps
    $BORDER = 240
    function Panel([int]$top, [int]$h, [string]$title, [int]$tc = 248) {  # a rounded panel with the title set into the top border
        $title = " $title "
        At $top $PX
        [void]$SB.Append($CC[$BORDER] + '╭─' + $BOLD + $CC[$tc] + $title + $RST + $CC[$BORDER] + ('─' * [Math]::Max(0, $PW - 3 - $title.Length)) + '╮' + $RST)
        $sideRow = $CC[$BORDER] + '│' + $RST + (' ' * ($PW - 2)) + $CC[$BORDER] + '│' + $RST
        for ($i = 1; $i -lt $h - 1; $i++) { At ($top + $i) $PX; [void]$SB.Append($sideRow) }
        At ($top + $h - 1) $PX; [void]$SB.Append($CC[$BORDER] + '╰' + ('─' * ($PW - 2)) + '╯' + $RST)
    }
    function Inner([int]$row) { At $row ($PX + 2); [void]$SB.Append(' ' * $IW); At $row ($PX + 2) }   # clear an inner row, cursor at its start
    function Pill([int]$c, [string]$t) { return ($BGC[$c] + $CC[234] + $BOLD + " $t " + $RST) }
    function Keys {  # row, then pairs of key and label
        $script:KR = [int]$args[0]; $script:KARGS = @($args)
        $row = [int]$args[0]; $o = ''; $w = 0
        for ($i = 1; $i + 1 -lt $args.Count; $i += 2) {
            $kc = [string]$args[$i]; $lb = [string]$args[$i + 1]
            $o += $BGC[238] + $CC[255] + $BOLD + " $kc " + $RST + $CC[248] + " $lb" + $RST + '    '
            $w += $kc.Length + $lb.Length + 7
        }
        $w -= 4                                              # no gap after the last pair
        At $row ((IDiv ($COLS - $w) 2) + 1); [void]$SB.Append($o)
    }

    # Leaving takes two presses: Ctrl+C (anywhere) or q (main menu, features), twice within 3 seconds.
    # The first press only shows a hint on the bottom row. Choosing Quit in the menu leaves at once.
    $script:LAST_QUIT = -100000; $script:HINT_UNTIL = 0; $script:KR = -1; $script:KARGS = @()
    function Quit-Key {
        $name = 'Ctrl+C'; if ($script:KEY -eq 'q') { $name = 'q' }
        if ($CLOCK.ElapsedMilliseconds - $script:LAST_QUIT -le 3000) { Farewell }
        $script:LAST_QUIT = $CLOCK.ElapsedMilliseconds
        Hint "Press $name again to quit"
    }
    function Hint([string]$t) {
        At $ROWS 1; [void]$SB.Append("$ESC[2K")
        At $ROWS ((IDiv ($COLS - $t.Length) 2) + 1); [void]$SB.Append($BOLD + $CC[222] + $t + $RST)
        $script:HINT_UNTIL = $CLOCK.ElapsedMilliseconds + 3000
    }
    function Hint-Tick {  # while waiting: the hint goes away after 3 seconds (and a key row on the bottom line comes back)
        if ($script:HINT_UNTIL -le 0 -or $CLOCK.ElapsedMilliseconds -lt $script:HINT_UNTIL) { return }
        $script:HINT_UNTIL = 0
        At $ROWS 1; [void]$SB.Append("$ESC[2K")
        if ($script:KR -eq $ROWS) { $ka = $script:KARGS; Keys @ka }
    }

    # ------------------------------------------------------------ big letters (3 rows, box drawing)
    $GLYPHS = @{
        'A' = @('╔═╗', '╠═╣', '╩ ╩'); 'C' = @('╔═╗', '║  ', '╚═╝'); 'D' = @('╔╦╗', ' ║║', '═╩╝'); 'E' = @('╔═╗', '║╣ ', '╚═╝')
        'F' = @('╔═╗', '╠╣ ', '╚  '); 'H' = @('╦ ╦', '╠═╣', '╩ ╩'); 'I' = @('╦', '║', '╩'); 'K' = @('╦╔═', '╠╩╗', '╩ ╩')
        'M' = @('╔╦╗', '║║║', '╩ ╩'); 'N' = @('╔╗╔', '║║║', '╝╚╝'); 'O' = @('╔═╗', '║ ║', '╚═╝'); 'R' = @('╦═╗', '╠╦╝', '╩╚═')
        'S' = @('╔═╗', '╚═╗', '╚═╝'); 'T' = @('╔╦╗', ' ║ ', ' ╩ '); 'U' = @('╦ ╦', '║ ║', '╚═╝'); 'Y' = @('╦ ╦', '╚╦╝', ' ╩ ')
        'L' = @('╦  ', '║  ', '╩═╝'); 'V' = @('╦  ╦', '╚╗╔╝', ' ╚╝ '); 'B' = @('╔╗ ', '╠╩╗', '╚═╝'); 'G' = @('╔═╗', '║ ╦', '╚═╝')
        'P' = @('╔═╗', '╠═╝', '╩  '); 'W' = @('╦ ╦', '║║║', '╚╩╝')
    }
    function Big-Rows([string]$word) {  # word -> its three rows
        $r0 = ''; $r1 = ''; $r2 = ''
        foreach ($ch in $word.ToCharArray()) {
            $g3 = $GLYPHS[[string]$ch]; if (-not $g3) { $g3 = @('  ', '  ', '  ') }
            $r0 += $g3[0]; $r1 += $g3[1]; $r2 += $g3[2]
        }
        return ,@($r0, $r1, $r2)
    }
    function Gradient([string]$t, [int[]]$palette, [int]$upto = -1) {  # the text coloured left to right, up to column upto
        $len = $t.Length; if ($upto -lt 0) { $upto = $len }; $n = $palette.Count
        $o = New-Object System.Text.StringBuilder 512; $last = ''
        for ($i = 0; $i -lt $upto -and $i -lt $len; $i++) {
            $ch = $t[$i]
            if ($ch -eq ' ') { [void]$o.Append(' '); continue }
            $col = $CC[$palette[[int][Math]::Floor($i * $n / $len)]]
            if ($col -ne $last) { [void]$o.Append($col); $last = $col }
            [void]$o.Append($ch)
        }
        [void]$o.Append($RST)
        return $o.ToString()
    }
    function Wrap-Text([string]$t, [int]$w, [int]$n) {  # word wrap into n lines; the last one ends in … when the text is longer
        $wl = New-Object string[] $n
        for ($i = 0; $i -lt $n; $i++) {
            if ($t.Length -le $w) { $wl[$i] = $t; $t = ''; continue }
            $cut = $w; while ($cut -gt 0 -and $t[$cut] -ne ' ') { $cut-- }
            if ($cut -eq 0) { $cut = $w }
            $wl[$i] = $t.Substring(0, $cut); $t = $t.Substring([Math]::Min($cut + 1, $t.Length))
        }
        if ($t) { $lastl = [string]$wl[$n - 1]; $wl[$n - 1] = $lastl.Substring(0, [Math]::Min($lastl.Length, $w - 1)) + '…' }
        return ,$wl
    }

    # ============================================================ the installer screens
    $LTOP = $SP; $LBOT = $Y0 + $BLOCK - 1        # the lower area every screen below draws in
    $DUSK = $PAL; $MINT = @(121, 120, 122, 123, 159, 153); $AMBER = @(222, 221, 215, 216, 217, 218); $ROSE = @(211, 212, 213, 218, 219, 183)

    function Clear-Lower { for ($r = $LTOP; $r -le $LBOT; $r++) { Clear-Line $r } }
    function Trunc([string]$t, [int]$w) { if ($t.Length -gt $w) { if ($w -lt 1) { return '' }; return $t.Substring(0, $w - 1) + '…' }; return $t }
    function Mid([string]$t, [int]$w) {  # shorten in the middle, so the start and the end both stay readable
        if ($t.Length -le $w) { return $t }
        $hh = IDiv ($w - 1) 2
        return $t.Substring(0, $hh) + '…' + $t.Substring($t.Length - ($w - 1 - $hh))
    }
    function Note-Right([int]$row, [string]$t, [int]$c = 246) { At $row ($PX + $PW - 3 - $t.Length); [void]$SB.Append($CC[$c] + $t + $RST) }
    function Toast([int]$row, [string]$t, [int]$c = 121) { Clear-Line $row; Center $row $t $CC[$c]; Nap 1600; Clear-Line $row }
    function Big-Word([int]$row, [string]$word, [int[]]$palette, [bool]$wipe = $false) {
        $big = Big-Rows $word
        $x = (IDiv ($COLS - $big[0].Length) 2) + 1
        if ($wipe) {
            for ($c = 1; $c -le $big[0].Length; $c += 2) {
                for ($i = 0; $i -lt 3; $i++) { At ($row + $i) $x; [void]$SB.Append((Gradient $big[$i] $palette $c)) }
                Nap 16
            }
        }
        for ($i = 0; $i -lt 3; $i++) { At ($row + $i) $x; [void]$SB.Append((Gradient $big[$i] $palette)) }
    }
    function Wait-For([string[]]$want) {  # wait for one of the given keys (Ctrl+C twice leaves); the stars keep twinkling
        while ($true) {
            Nap 80; Twinkle
            if (-not (Poll-Key)) { continue }
            if ($script:KEY -eq 'ctrlc') { Quit-Key; continue }
            if ($want -contains $script:KEY) { return }
        }
    }
    function Num([string]$t) { return ($BOLD + $CC[222] + $t + '  ' + $RST) }

    # ---- steps: a spinner while each piece of real work runs, then a check or a cross
    function Step-Wait([int]$row, [string]$label) { Inner $row; [void]$SB.Append($CC[240] + '○  ' + $label + $RST) }
    function Progress-Bar([int]$row, [int]$pct) {
        $w = $IW - 6; $fill = [int][Math]::Floor($w * $pct / 100)
        $o = New-Object System.Text.StringBuilder 1024
        for ($i = 0; $i -lt $w; $i++) {
            if ($i -lt $fill) { [void]$o.Append($CC[$PAL[[int][Math]::Floor($i * $PAL.Count / $w)]]).Append('━') }
            else { [void]$o.Append($CC[238]).Append('─') }
        }
        Inner $row; [void]$SB.Append($o.ToString() + $RST + ' ' + $CC[250] + ('{0,3}%' -f $pct) + $RST)
    }
    function Run-Steps([string]$title, [int]$accent, [string[]]$steps) {  # "label|detail|function" entries; $false when a step fails
        $n = $steps.Count; $pct = 0
        Clear-Lower; Panel $LTOP ($n + 5) $title $accent
        for ($i = 0; $i -lt $n; $i++) { Step-Wait ($LTOP + 2 + $i) ($steps[$i].Split('|')[0]) }
        Progress-Bar ($LTOP + $n + 3) 0
        for ($i = 0; $i -lt $n; $i++) {
            $parts = $steps[$i].Split('|'); $label = $parts[0]; $detail = $parts[1]; $fn = $parts[2]
            $row = $LTOP + 2 + $i
            for ($f = 0; $f -lt 6; $f++) {
                Inner $row; [void]$SB.Append($CC[218] + $SPINNER[$f % $SPINNER.Count] + $RST + '  ' + $CC[253] + $label + $RST)
                Twinkle; Nap 60
            }
            $script:ERR = ''
            $ok = $false
            try { $ok = [bool](& $fn) } catch { if (-not $script:ERR) { $script:ERR = $_.Exception.Message }; $ok = $false }
            if ($ok) {
                Inner $row; [void]$SB.Append($CC[121] + $GL.CHECK + $RST + '  ' + $BOLD + $CC[253] + $label + $RST)
                if ($detail) { Note-Right $row $detail 246 }
            } else {
                Inner $row; [void]$SB.Append($CC[203] + $GL.CROSS + $RST + '  ' + $BOLD + $CC[253] + $label + $RST)
                Note-Right $row 'failed' 203
                Nap 800
                return $false
            }
            $target = [int][Math]::Floor(100 * ($i + 1) / $n)
            while ($pct -lt $target) { $pct += 5; if ($pct -gt $target) { $pct = $target }; Progress-Bar ($LTOP + $n + 3) $pct; Nap 20 }
        }
        Nap 400
        return $true
    }
    function Error-Screen([string]$title) {  # the message is in ERR
        Clear-Lower; Panel $LTOP 8 $title 203
        $msg = $script:ERR; if (-not $msg) { $msg = 'Something went wrong.' }
        $wl = Wrap-Text $msg $IW 3
        for ($i = 0; $i -lt 3; $i++) { Inner ($LTOP + 1 + $i); [void]$SB.Append($CC[253] + $wl[$i] + $RST) }
        Inner ($LTOP + 5); [void]$SB.Append($CC[248] + (Trunc 'Nothing was deleted. Details: BepInEx\cwyls-installer.log' $IW) + $RST)
        Keys ($LTOP + 9) 'enter' 'back to menu'
        Log-Line "error: $($script:ERR)"
        Wait-For 'enter', 'esc'
    }

    # ---- buttons and the confirm dialog (the safe choice is selected first)
    function Buttons([int]$row, [int]$selected, [string]$l0, [string]$l1, [int]$accent, [string]$what) {
        # The selected button is a filled pill with a marker; the other is plain text. Reads the same on
        # light and dark backgrounds (a dark filled box must never look like the selected one).
        if ($selected -eq 0) { $ba = $BGC[121] + $CC[234] + $BOLD + " › $l0 " + $RST; $bb = $CC[246] + "   $l1  " + $RST }
        else { $ba = $CC[246] + "   $l0  " + $RST; $bb = $BGC[$accent] + $CC[234] + $BOLD + " › $l1 " + $RST }
        $w = $l0.Length + $l1.Length + 14
        Inner $row; At $row ((IDiv ($COLS - $w) 2) + 1); [void]$SB.Append($ba + '    ' + $bb)
        if ($selected -eq 0) { $will = $CC[246] + 'Enter will: ' + $CC[253] + $BOLD + $l0.ToLower() + $RST }
        else { $wh = $what; if (-not $wh) { $wh = $l1 }; $will = $CC[246] + 'Enter will: ' + $CC[$accent] + $BOLD + $wh + $RST }
        Inner ($row + 1); At ($row + 1) ($PX + 3); [void]$SB.Append($will)
    }
    function Confirm-Box([string]$title, [int]$accent, [string]$line1, [string]$line2, [string]$l0, [string]$l1, [string]$what) {  # $true when confirmed
        $choice = 0
        Clear-Lower; Panel $LTOP 8 $title $accent
        Inner ($LTOP + 1); [void]$SB.Append($CC[253] + (Trunc $line1 $IW) + $RST)
        Inner ($LTOP + 2); [void]$SB.Append($CC[248] + (Trunc $line2 $IW) + $RST)
        Buttons ($LTOP + 4) $choice $l0 $l1 $accent $what
        Keys ($LTOP + 9) '←→' 'choose' 'enter' 'confirm' 'esc' 'back'
        while ($true) {
            Wait-For 'left', 'right', 'enter', 'esc'
            if ($script:KEY -eq 'left' -or $script:KEY -eq 'right') { $choice = 1 - $choice; Buttons ($LTOP + 4) $choice $l0 $l1 $accent $what }
            elseif ($script:KEY -eq 'esc') { return $false }
            elseif ($script:KEY -eq 'enter') { return ($choice -eq 1) }
        }
    }

    # ---- the game is running: wait until it closes (esc cancels)
    function Ensure-Closed {
        if (-not (Test-GameRunning)) { return $true }
        $f = 0
        Clear-Lower; Panel $LTOP 7 'THE GAME IS RUNNING' 222
        Inner ($LTOP + 1); [void]$SB.Append($CC[253] + (Trunc 'Quit Chill With You first: its files cannot change while it runs.' $IW) + $RST)
        Inner ($LTOP + 2); [void]$SB.Append($CC[246] + (Trunc 'Your progress is saved by the game as usual.' $IW) + $RST)
        Keys ($LTOP + 8) 'esc' 'cancel'
        while (Test-GameRunning) {
            Inner ($LTOP + 4); [void]$SB.Append($CC[222] + $SPINNER[$f % $SPINNER.Count] + $RST + '  ' + $CC[248] + 'waiting for the game to close…' + $RST)
            $f++; Nap 80; Twinkle
            if (Poll-Key) {
                if ($script:KEY -eq 'ctrlc') { Quit-Key }
                if ($script:KEY -eq 'esc') { return $false }
            }
        }
        Inner ($LTOP + 4); [void]$SB.Append($CC[121] + $GL.CHECK + $RST + '  ' + $BOLD + $CC[253] + 'The game is closed. Carrying on…' + $RST)
        Nap 1000
        return $true
    }

    # ---- the main screen: your setup + the menu
    function Refresh-State {
        $script:IV = Installed-Version
        $script:BEP = BepInEx-Installed
        $script:LOAD = Loader-Installed
        $script:TRACE_STATE = Trace-Level
        $script:MODS = Mods-Count
        $script:STEAM_OK = $script:BEP       # winhttp.dll starts BepInEx whenever Steam starts the game
    }
    function Set-ShortPath {
        $script:SHORT_PATH = $script:GAME_ROOT
        if ($script:SHORT_PATH.Length -gt $IW - 12) { $script:SHORT_PATH = '…' + $script:SHORT_PATH.Substring($script:SHORT_PATH.Length - ($IW - 13)) }
    }
    function Setup-Row([int]$row, [string]$label) { Inner $row; [void]$SB.Append($CC[247] + $label.PadRight(12) + $RST) }
    function Draw-Setup {
        $vw = $IW - 15
        Panel $SP 6 'YOUR SETUP'
        Setup-Row ($SP + 1) 'Game'; [void]$SB.Append($CC[121] + $GL.CHECK + $RST + '  ' + $BOLD + $CC[253] + 'Chill With You: Lo-Fi Story' + $RST)
        Setup-Row ($SP + 2) 'Location'; [void]$SB.Append($CC[250] + $script:SHORT_PATH + $RST)
        Setup-Row ($SP + 3) 'Mod loader'
        if ($script:LOAD -and $script:IV -eq $VERSION) {
            [void]$SB.Append($CC[121] + $GL.CHECK + $RST + '  ' + $CC[253] + (Trunc "$VERSION_UI installed, with BepInEx $BEPINEX_VERSION" $vw) + $RST)
        } elseif ($script:LOAD) {
            $ivs = $script:IV; if (-not $ivs) { $ivs = '?' }
            [void]$SB.Append($CC[117] + '↑' + $RST + '  ' + $CC[253] + (Trunc "v$(($ivs -split '-')[0]) → $VERSION_UI" ($vw - 8)) + $RST + '  ' + (Pill 117 'NEW'))
        } elseif ($script:BEP) {
            [void]$SB.Append($CC[222] + '○' + $RST + '  ' + $CC[253] + (Trunc 'not installed (BepInEx is)' $vw) + $RST)
        } else {
            [void]$SB.Append($CC[222] + '○' + $RST + '  ' + $CC[253] + (Trunc 'not installed yet' $vw) + $RST)
        }
        Setup-Row ($SP + 4) 'Steam'
        if ($script:STEAM_OK) { [void]$SB.Append($CC[121] + $GL.CHECK + $RST + '  ' + $CC[253] + (Trunc 'starts the game through BepInEx' $vw) + $RST) }
        else { [void]$SB.Append($CC[242] + '–  ' + (Trunc 'nothing to set in Steam' $vw) + $RST) }
    }
    $MENU = @('Install or update', 'Diagnostic trace', 'Open the mods folder', 'Uninstall the mod loader', 'Uninstall everything', 'Quit')
    function Menu-Meta {
        $mm = @('', '', '', '', '', '')
        if ($script:LOAD -and $script:IV -ne $VERSION) { $mm[0] = '@117:UPDATE' }
        elseif ($script:LOAD) { $mm[0] = 'reinstall' } else { $mm[0] = 'BepInEx + loader' }
        if ($script:TRACE_STATE -eq 'off') { $mm[1] = '@244:OFF' } elseif ($script:TRACE_STATE -eq 'on') { $mm[1] = '@121:ON' } elseif ($script:TRACE_STATE -eq 'full') { $mm[1] = '@222:FULL' }
        if ($script:MODS -eq 1) { $mm[2] = '1 mod' } elseif ($script:MODS -gt 1) { $mm[2] = "$($script:MODS) mods" } else { $mm[2] = 'empty' }
        if ($script:LOAD) { $mm[3] = 'keeps BepInEx' } else { $mm[3] = 'not installed' }
        if ($script:LOAD -or $script:BEP) { $mm[4] = 'moves files aside' } else { $mm[4] = 'nothing installed' }
        $script:META = $mm
    }
    function Draw-Item([int]$i) {
        $y = $MP + 1 + $i; $meta = [string]$script:META[$i]; $isSel = ($i -eq $script:MSEL)
        At $y ($PX + 1)
        if ($isSel) { [void]$SB.Append($BGC[236] + $CC[218] + '▌ ' + $BOLD + $CC[255] + $MENU[$i].PadRight($IW - 1)) }
        else { [void]$SB.Append($RST + '  ' + $CC[252] + $MENU[$i].PadRight($IW - 1)) }
        if ($meta.StartsWith('@')) {
            $pc = [int]$meta.Substring(1).Split(':')[0]; $pt = $meta.Substring(1).Split(':')[1]
            At $y ($PX + $PW - 3 - $pt.Length - 2); [void]$SB.Append((Pill $pc $pt))
        } elseif ($meta) {
            At $y ($PX + $PW - 3 - $meta.Length)
            if ($isSel) { [void]$SB.Append($BGC[236] + $CC[252] + $meta + $RST) } else { [void]$SB.Append($CC[246] + $meta + $RST) }
        }
        if ($isSel) { At $y ($PX + $PW - 2); [void]$SB.Append($BGC[236] + ' ' + $RST) }
        [void]$SB.Append($RST)
    }
    function Draw-Menu { Menu-Meta; for ($i = 0; $i -lt $MENU.Count; $i++) { Draw-Item $i } }
    $script:NOTICE = ''
    function Draw-Main {
        Refresh-State
        Clear-Lower; Draw-Setup
        Panel $MP ($MENU.Count + 2) 'WHAT WOULD YOU LIKE TO DO'
        Draw-Menu
        if ($script:NOTICE) { Toast $KEYS2 $script:NOTICE 222; $script:NOTICE = '' }
        Keys $KEYS2 '↑↓' 'move' 'enter' 'choose' 'q' 'quit'
    }

    # ---- 1. install or update
    function Act-Install {
        if (-not (Ensure-Closed)) { $script:NOTICE = 'Nothing changed.'; return }
        $title = 'INSTALLING'; if ($script:LOAD) { $title = 'UPDATING' }
        $steps = @('Game is closed|checked|Do-Nothing')
        if (Needs-Migration) { $steps += 'An earlier build|tidied up, nothing deleted|Do-Migrate' }
        $steps += "BepInEx $BEPINEX_VERSION|stock, unmodified|Do-BepInEx"
        $steps += "CWY:LS Mod Loader $VERSION_UI|$(@(Loader-Dlls).Count) files|Do-Loader"
        $steps += "Your mods folder|$MODS_DIR_NAME|Do-ModsFolder"
        if (-not (Run-Steps $title 218 $steps)) { Error-Screen 'THE INSTALL STOPPED'; return }
        Log-Line "installed $VERSION"
        $SUMMARY.Add("Installed CWY:LS Mod Loader $VERSION_UI and BepInEx $BEPINEX_VERSION in $($script:GAME_ROOT)")
        Refresh-State
        Clear-Lower
        Big-Word $LTOP 'ALL SET' $MINT $true
        $others = Other-Mods
        if ($others) {
            Center ($LTOP + 3) 'Installed. Your save files were not touched.' $CC[252]
            Center ($LTOP + 4) (Trunc "Also in BepInEx\plugins: $others (not tested with CWY:LS)" ($COLS - 4)) $CC[222]
        } else {
            Center ($LTOP + 4) 'Installed. Your save files were not touched.' $CC[252]
        }
        # No Steam step on Windows: winhttp.dll starts BepInEx whenever the game starts.
        Center ($LTOP + 6) 'Start the game from Steam as usual.' ($BOLD + $CC[253])
        Center ($LTOP + 7) 'ctrl shift C opens the console.' $CC[246]
        Keys ($LTOP + 9) 'enter' 'back to menu'
        Wait-For 'enter'
    }

    # ---- 2. diagnostic trace
    $TRACE_NAMES = @('Off', 'On', 'Full'); $TRACE_VALS = @('off', 'on', 'full'); $TRACE_ACC = @(244, 121, 222)
    $TRACE_DESCS = @('nothing is recorded', 'key events, small files: for bug reports', 'every game method, large files: for developers')
    function Draw-TraceOpts([int]$cur) {
        for ($i = 0; $i -lt 3; $i++) {
            $y = $LTOP + 4 + $i; Inner $y
            if ($i -eq $cur) { [void]$SB.Append($CC[$TRACE_ACC[$i]] + '●' + $RST + '  ' + $BOLD + $CC[255] + $TRACE_NAMES[$i].PadRight(6) + $RST) }
            else { [void]$SB.Append($CC[240] + '○  ' + $CC[248] + $TRACE_NAMES[$i].PadRight(6) + $RST) }
            [void]$SB.Append('  ' + $CC[246] + (Trunc $TRACE_DESCS[$i] ($IW - 22)) + $RST)
            if ($TRACE_VALS[$i] -eq $script:TRACE_STATE) { At $y ($PX + $PW - 3 - 9); [void]$SB.Append((Pill 238 'CURRENT')) }
        }
    }
    function Act-Trace {
        $cur = 0
        if ($script:TRACE_STATE -eq 'on') { $cur = 1 } elseif ($script:TRACE_STATE -eq 'full') { $cur = 2 }
        Clear-Lower; Panel $LTOP 9 'DIAGNOSTIC TRACE' 183
        Inner ($LTOP + 1); [void]$SB.Append($CC[252] + (Trunc 'Records what the game and the mod loader do, so a bug can be found.' $IW) + $RST)
        Inner ($LTOP + 2); [void]$SB.Append($CC[246] + (Trunc 'It starts with the next game launch. Files go to BepInEx\trace.' $IW) + $RST)
        Draw-TraceOpts $cur
        Keys ($LTOP + 10) '↑↓' 'choose' 'enter' 'apply' 'esc' 'back'
        while ($true) {
            Wait-For 'up', 'down', 'enter', 'esc'
            if ($script:KEY -eq 'up') { $cur = ($cur + 2) % 3; Draw-TraceOpts $cur }
            elseif ($script:KEY -eq 'down') { $cur = ($cur + 1) % 3; Draw-TraceOpts $cur }
            elseif ($script:KEY -eq 'esc') { return }
            elseif ($script:KEY -eq 'enter') {
                if (Set-Trace $TRACE_VALS[$cur]) {
                    $script:TRACE_STATE = $TRACE_VALS[$cur]; Draw-TraceOpts $cur; Log-Line "trace $($TRACE_VALS[$cur])"
                    $SUMMARY.Add("Diagnostic trace: $($TRACE_NAMES[$cur]) (from the next game start)")
                    Toast ($LTOP + 10) "$($GL.CHECK)  Trace set to $($TRACE_NAMES[$cur].ToUpper()), from the next game start" 121
                } else {
                    Toast ($LTOP + 10) "$($GL.CROSS)  $($script:ERR)" 203
                }
                return
            }
        }
    }

    # ---- 3. the mods folder
    function Act-Open {
        [void](Do-ModsFolder)
        $modsDir = Game-Path $MODS_DIR_NAME
        $opened = 'Here it is'
        if ($TTY_OUT) { try { Invoke-Item -LiteralPath $modsDir; $opened = 'Opened in File Explorer' } catch { } }
        Refresh-State
        Clear-Lower; Panel $LTOP 7 'YOUR MODS FOLDER' 117
        Inner ($LTOP + 1); [void]$SB.Append($CC[121] + $GL.CHECK + $RST + '  ' + $BOLD + $CC[253] + $opened + $RST)
        $count = "$($script:MODS) mods"; if ($script:MODS -eq 1) { $count = '1 mod' }
        Note-Right ($LTOP + 1) $count 246
        Inner ($LTOP + 2); [void]$SB.Append('   ' + $CC[250] + (Mid $modsDir ($IW - 3)) + $RST)
        Inner ($LTOP + 4); [void]$SB.Append($CC[252] + (Trunc 'Each mod is a folder with a manifest.json. Drop it in here.' $IW) + $RST)
        Inner ($LTOP + 5); [void]$SB.Append($CC[246] + (Trunc 'The game reads this folder when it starts. Only add mods you trust.' $IW) + $RST)
        Keys ($LTOP + 8) 'enter' 'back to menu'
        Wait-For 'enter', 'esc'
    }

    # ---- 4. uninstall the mod loader
    function Act-Uninstall {  # $false when there was nothing to do (the menu stays as it is)
        if (-not $script:LOAD) { Toast $KEYS2 'The mod loader is not installed.' 222; Keys $KEYS2 '↑↓' 'move' 'enter' 'choose' 'q' 'quit'; return $false }
        if (-not (Confirm-Box 'REMOVE THE MOD LOADER?' 222 "BepInEx and your `"$MODS_DIR_NAME`" folder stay where they are." `
                'The loader is moved to BepInEx\_old, never deleted.' 'Keep it' 'Remove' 'REMOVE the mod loader')) {
            $script:NOTICE = 'Nothing changed: the mod loader is still installed.'; return $true
        }
        if (-not (Ensure-Closed)) { $script:NOTICE = 'Nothing changed.'; return $true }
        if (-not (Run-Steps 'REMOVING' 222 @('Game is closed|checked|Do-Nothing', 'CWY:LS Mod Loader|moved to BepInEx\_old|Do-UninstallLoader'))) {
            Error-Screen 'NOTHING WAS REMOVED'; return $true
        }
        Log-Line "uninstalled the loader -> $($script:MOVED_TO)"
        $SUMMARY.Add("Removed the mod loader (moved to $($script:MOVED_TO))")
        Clear-Lower
        Big-Word $LTOP 'REMOVED' $AMBER $true
        Center ($LTOP + 4) 'Your saves were not touched. Reinstall any time from the menu.' $CC[252]
        Center ($LTOP + 5) (Mid "Moved to $($script:MOVED_TO); delete it whenever you like." ($COLS - 4)) $CC[246]
        Keys ($LTOP + 7) 'enter' 'back to menu'
        Wait-For 'enter'
        return $true
    }

    # ---- 5. uninstall everything
    function Act-UninstallAll {  # $false when there was nothing to do
        if (-not $script:LOAD -and -not $script:BEP) { Toast $KEYS2 'Nothing to remove: BepInEx and the loader are not installed.' 222; Keys $KEYS2 '↑↓' 'move' 'enter' 'choose' 'q' 'quit'; return $false }
        if (-not (Confirm-Box 'REMOVE EVERYTHING?' 211 'BepInEx and the mod loader leave the game folder.' `
                "Your `"$MODS_DIR_NAME`" folder and your saves stay. Nothing is deleted." 'Keep everything' 'Remove all' 'REMOVE BepInEx and the mod loader')) {
            $script:NOTICE = 'Nothing changed: BepInEx and the mod loader are still installed.'; return $true
        }
        if (-not (Ensure-Closed)) { $script:NOTICE = 'Nothing changed.'; return $true }
        Log-Line 'removing BepInEx and the mod loader'
        if (-not (Run-Steps 'REMOVING EVERYTHING' 211 @('Game is closed|checked|Do-Nothing', 'BepInEx and the mod loader|moved aside|Do-UninstallAll'))) {
            Error-Screen 'NOTHING WAS REMOVED'; return $true
        }
        $SUMMARY.Add("Removed BepInEx and the mod loader (moved to $($script:MOVED_TO)).")
        Clear-Lower
        Big-Word $LTOP 'ALL CLEAN' $ROSE $true
        Center ($LTOP + 4) 'The game is back to how it shipped. Your saves were not touched.' $CC[252]
        Center ($LTOP + 5) (Mid "Moved to $($script:MOVED_TO) in the game folder." ($COLS - 4)) $CC[246]
        Keys ($LTOP + 7) 'enter' 'back to menu'
        Wait-For 'enter'
        return $true
    }

    # ---- the game was not found / this is the demo
    function Read-PromptLine([int]$row) {  # typed or dragged-in text at the prompt, until Enter (Ctrl+C twice leaves)
        $buf = ''
        while ($true) {
            $max = $IW - 3; $vis = $buf
            if ($vis.Length -gt $max) { $vis = '…' + $vis.Substring($vis.Length - $max + 1) }
            Inner $row; [void]$SB.Append($CC[218] + '›' + $RST + ' ' + $CC[253] + $vis + $RST)
            At $row ($PX + 4 + $vis.Length); [void]$SB.Append("$ESC[?25h"); Flush-Screen
            if ($Scripted) {
                if ($KEYQ.Count -eq 0) { $tok = 'ctrlc' } else { $tok = $KEYQ.Dequeue() }
                if ($tok.StartsWith('text:')) { [void]$SB.Append("$ESC[?25l"); return $tok.Substring(5) }
                if ($tok -eq 'enter') { [void]$SB.Append("$ESC[?25l"); return $buf }
                if ($tok -match '^sleep:(\d+)$') { Nap ([int]$Matches[1]); Hint-Tick; continue }
                if ($tok -eq 'ctrlc') { [void]$SB.Append("$ESC[?25l"); $script:KEY = 'ctrlc'; Quit-Key }
                continue
            }
            while (-not [Console]::KeyAvailable) {
                Start-Sleep -Milliseconds 40
                if ($script:HINT_UNTIL -gt 0 -and $CLOCK.ElapsedMilliseconds -ge $script:HINT_UNTIL) { Hint-Tick; At $row ($PX + 4 + $vis.Length); Flush-Screen }
            }
            $k = [Console]::ReadKey($true)
            if (Is-CtrlC $k) { [void]$SB.Append("$ESC[?25l"); $script:KEY = 'ctrlc'; Quit-Key; continue }
            if ($k.Key -eq [ConsoleKey]::Enter) { [void]$SB.Append("$ESC[?25l"); return $buf }
            if ($k.Key -eq [ConsoleKey]::Backspace) { if ($buf.Length -gt 0) { $buf = $buf.Substring(0, $buf.Length - 1) }; continue }
            if ([int]$k.KeyChar -ge 32) { $buf += [string]$k.KeyChar }
        }
    }
    function Screen-NotFound([string]$problem) {
        while ($true) {
            Clear-Lower; Panel $LTOP 8 'WHERE IS THE GAME?' 117
            Inner ($LTOP + 1); [void]$SB.Append($CC[253] + (Trunc 'I could not find Chill With You in your Steam libraries.' $IW) + $RST)
            Inner ($LTOP + 2); [void]$SB.Append($CC[246] + (Trunc "Drag the game's folder in here and press Enter." $IW) + $RST)
            if ($problem) { Inner ($LTOP + 6); [void]$SB.Append($CC[203] + $GL.CROSS + $RST + '  ' + $CC[252] + (Trunc $problem ($IW - 3)) + $RST) }
            $line = Read-PromptLine ($LTOP + 4)
            if (-not $line.Trim()) { $problem = 'Drag the game folder in here, or press Ctrl+C twice to quit.'; continue }
            $line = Clean-Path $line
            if (Is-Game $line) { $script:GAME_ROOT = $line; return }
            $problem = "That folder has no `"$EXE`" in it. Try again."
        }
    }
    function Screen-Demo {
        Clear-Lower; Panel $LTOP 7 'THIS LOOKS LIKE THE DEMO' 211
        Inner ($LTOP + 1); [void]$SB.Append($CC[253] + (Trunc 'Mods need the full game. Nothing was installed.' $IW) + $RST)
        Inner ($LTOP + 2); [void]$SB.Append($CC[246] + (Trunc 'The demo stays exactly as it is.' $IW) + $RST)
        Inner ($LTOP + 4); [void]$SB.Append((Pill 211 'FULL GAME ONLY') + '  ' + $CC[252] + (Trunc 'Get it on Steam for mods.' ($IW - 20)) + $RST)
        Keys ($LTOP + 8) 'enter' 'close'
        Wait-For 'enter', 'esc'
        Farewell
    }

    # ---- goodbye: SEE YOU, then the name scatters back into stars
    function Farewell {
        $script:FAST = $false
        Clear-Lower
        Big-Word ($LTOP + 1) 'SEE YOU' $DUSK $true
        Center ($LTOP + 5) 'Happy modding.' $CC[250]
        Nap 900
        for ($p = 0; $p -lt $NP; $p++) {
            $script:PSX[$p] = $rng.Next($COLS) + 1; $script:PSY[$p] = $rng.Next($ROWS) + 1; $script:PD[$p] = $rng.Next(8)
            $script:OX[$p] = $TX[$p]; $script:OY[$p] = $TYP[$p]
        }
        for ($i = 0; $i -lt 6; $i++) { Clear-Row ($Y0 + $i) }
        for ($f = 0; $f -lt $NE + 8; $f++) {
            for ($p = 0; $p -lt $NP; $p++) {
                $k = $f - $PD[$p]; if ($k -lt 0) { $k = 0 }; if ($k -ge $NE) { $k = $NE - 1 }
                $ev = 1000 - $EASE[$NE - 1 - $k]
                $x = $TX[$p] + [int][Math]::Truncate(($PSX[$p] - $TX[$p]) * $ev / 1000)
                $y = $TYP[$p] + [int][Math]::Truncate(($PSY[$p] - $TYP[$p]) * $ev / 1000)
                if ($OX[$p] -ne $x -or $OY[$p] -ne $y) { [void]$SB.Append("$ESC[$($OY[$p]);$($OX[$p])H ") }
                if ($ev -lt 150) { [void]$SB.Append("$ESC[$y;${x}H" + $CC[$TC[$p]] + '█') }
                elseif ($ev -lt 600) { [void]$SB.Append("$ESC[$y;${x}H" + $CC[$TC[$p]] + '•') }
                else { [void]$SB.Append("$ESC[$y;${x}H" + $CC[250 - [int][Math]::Floor($ev / 200)] + '·') }
                $script:OX[$p] = $x; $script:OY[$p] = $y
            }
            [void]$SB.Append($RST); Nap 30
        }
        Nap 400
        for ($p = 0; $p -lt $NP; $p++) { At $OY[$p] $OX[$p]; [void]$SB.Append(' ') }
        for ($s = 0; $s -lt $SX.Count; $s++) { Star $s 0 '·' }
        Nap 250
        exit 0
    }

    # ============================================================ the intro
    # The stardust particles (also used by the goodbye, which can come at any time).
    $EASE = @(0, 150, 284, 403, 508, 600, 680, 748, 806, 854, 894, 925, 950, 969, 982, 991, 996, 999, 1000, 1000)
    $NE = $EASE.Count
    $TXL = New-Object System.Collections.Generic.List[int]; $TYL = New-Object System.Collections.Generic.List[int]; $TCL = New-Object System.Collections.Generic.List[int]
    for ($i = 0; $i -lt 6; $i++) {
        $line = $ART[$i]; $len = $line.Length
        for ($j = 0; $j -lt $len; $j++) {
            if ($line[$j] -ne '█') { continue }
            $TXL.Add($X0 + $j); $TYL.Add($Y0 + $i); $TCL.Add($PAL[[int][Math]::Floor($j * $PAL.Count / $len)])
        }
    }
    $TX = $TXL.ToArray(); $TYP = $TYL.ToArray(); $TC = $TCL.ToArray()
    $NP = $TX.Count
    $PSX = New-Object int[] $NP; $PSY = New-Object int[] $NP; $PD = New-Object int[] $NP; $OX = New-Object int[] $NP; $OY = New-Object int[] $NP
    for ($p = 0; $p -lt $NP; $p++) { $PSX[$p] = $rng.Next($COLS) + 1; $PSY[$p] = $rng.Next($ROWS) + 1; $PD[$p] = $rng.Next(8) }
    # 0. Night falls.
    for ($s = 0; $s -lt $SX.Count; $s++) { Star $s ($rng.Next(3)) '·'; if ($s % 4 -eq 0) { Frame 12 } }

    # 1. Stardust: points of light fly in from all over the sky and assemble into CWY:LS.
    for ($f = 0; $f -lt $NE + 8; $f++) {
        for ($p = 0; $p -lt $NP; $p++) {
            $k = $f - $PD[$p]; if ($k -lt 0) { continue }; if ($k -ge $NE) { $k = $NE - 1 }
            $ev = $EASE[$k]
            $x = $PSX[$p] + [int][Math]::Truncate(($TX[$p] - $PSX[$p]) * $ev / 1000)
            $y = $PSY[$p] + [int][Math]::Truncate(($TYP[$p] - $PSY[$p]) * $ev / 1000)
            if ($OX[$p] -gt 0 -and ($OX[$p] -ne $x -or $OY[$p] -ne $y)) { [void]$SB.Append("$ESC[$($OY[$p]);$($OX[$p])H ") }
            if ($ev -ge 1000) { [void]$SB.Append("$ESC[$y;${x}H" + $CC[$TC[$p]] + '█') }
            elseif ($ev -gt 820) { [void]$SB.Append("$ESC[$y;${x}H" + $CC[$TC[$p]] + '•') }
            else { [void]$SB.Append("$ESC[$y;${x}H" + $CC[244 + [int][Math]::Floor($ev / 170)] + '·') }
            $OX[$p] = $x; $OY[$p] = $y
        }
        [void]$SB.Append($RST)
        Frame 30
        if ($script:FAST) { break }
    }
    # The flash: all white for a moment, then the colours and the shadow settle in.
    for ($i = 0; $i -lt 6; $i++) { Clear-Row ($Y0 + $i); At ($Y0 + $i) $X0; [void]$SB.Append($CC[231] + $ART[$i] + $RST) }
    Frame 70
    for ($i = 0; $i -lt 6; $i++) { At ($Y0 + $i) $X0; [void]$SB.Append($PAINTED[$i]) }
    Frame 120

    # 2. A shine sweeps across the letters.
    for ($h = 0; $h -le $ARTW + 4; $h += 3) {
        if ($script:FAST) { break }
        for ($i = 0; $i -lt 6; $i++) { At ($Y0 + $i) $X0; [void]$SB.Append((Paint $ART[$i] ($h - $i))) }
        Frame 16
    }
    for ($i = 0; $i -lt 6; $i++) { At ($Y0 + $i) $X0; [void]$SB.Append($PAINTED[$i]) }

    # 3. The horizon grows from the middle; the caption fades in.
    $MID = $X0 + (IDiv $ARTW 2)
    for ($r = 0; $r -le (IDiv $ARTW 2); $r += 2) {
        At $HR ($MID - $r); [void]$SB.Append($CC[238] + ('─' * ($r * 2 + 1)) + $RST)
        Frame 10
    }
    foreach ($lv in @(236, 240, 244, 248, 252)) {
        if ($MODE -eq 'full') {
            Center $CAP1 'M O D   L O A D E R' $CC[$lv]
            Center $CAP2 "$VERSION_UI  ·  developer preview" ($CC[[Math]::Max($lv - 8, 232)])
        } else {
            Center $CAP1 "M O D   L O A D E R   ·   $VERSION_UI" $CC[$lv]
        }
        Frame 50
    }

    if ($FIRST_RUN) {
        # 4. THANK YOU, big, wiped in from the left in the dusk colours.
        $B = Big-Rows 'THANK  YOU'
        $BX = (IDiv ($COLS - $B[0].Length) 2) + 1
        for ($c = 1; $c -le $B[0].Length; $c += 2) {
            At $TY $BX; [void]$SB.Append((Gradient $B[0] $PAL $c))
            At ($TY + 1) $BX; [void]$SB.Append((Gradient $B[1] $PAL $c))
            At ($TY + 2) $BX; [void]$SB.Append((Gradient $B[2] $PAL $c))
            Frame 18
        }
        At $TY $BX; [void]$SB.Append((Gradient $B[0] $PAL)); At ($TY + 1) $BX; [void]$SB.Append((Gradient $B[1] $PAL)); At ($TY + 2) $BX; [void]$SB.Append((Gradient $B[2] $PAL))
        $T2 = 'for choosing CWY:LS Mod Loader'
        foreach ($lv in @(240, 244, 248, 252)) {
            At ($TY + 4) ((IDiv ($COLS - $T2.Length) 2) + 1)
            $nc = 218; if ($lv -lt 248) { $nc = $lv }
            [void]$SB.Append($CC[$lv] + 'for choosing ' + $BOLD + $CC[$nc] + 'CWY:LS Mod Loader' + $RST)
            Frame 50
        }
        Frame 1300

        # 5. What it can do: a panel with one feature at a time, its name in big letters.
        for ($r = $MSG; $r -le $Y0 + $BLOCK - 1; $r++) { Clear-Line $r }
        $ACC = @(218, 216, 183, 117, 121)
        $WORDS = @('MUSIC', 'SCENERY', 'STORY', 'CODE', 'SAFE')
        $TITLES = @('Music and sound', 'Scenery and colour', 'Lines and languages', 'Code mods', 'Your saves stay yours')
        $TEXTS = @(
            'Swap any game sound, or add your own songs to the music player.',
            'Recolour the room: a warm film look, a rainy evening, or your own.',
            'New lines and short scenes for Satone, or a whole new language.',
            'A documented C# API, built to keep working when the game updates.',
            'Nothing goes into your save files, and every launch backs them up.'
        )
        $NCARD = $TITLES.Count
        $BIGW = 0; foreach ($wd in $WORDS) { $bw = (Big-Rows $wd)[0].Length; if ($bw -gt $BIGW) { $BIGW = $bw } }
        $TXC = $PX + 3 + $BIGW + 3                       # text column
        $TW = $PX + $PW - 3 - $TXC                       # text width
        function Segments([int]$cur) {  # the page indicator, set into the panel's bottom border
            $w = $NCARD * 4 - 1; $seg = ''
            for ($i = 0; $i -lt $NCARD; $i++) {
                if ($i -eq $cur) { $seg += $CC[$ACC[$i]] + '━━━' + $RST } else { $seg += $CC[238] + '───' + $RST }
                if ($i -lt $NCARD - 1) { $seg += $CC[$BORDER] + '─' + $RST }
            }
            At ($FP + 6) ($PX + (IDiv ($PW - $w) 2)); [void]$SB.Append($seg)
        }
        function Card([int]$cur, [int]$step) {  # feature cur at brightness step 0..3, for the fade
            $tl = @(240, 245, 250, 255); $bl = @(240, 244, 248, 252)
            $accent = $ACC[$cur]
            if ($step -eq 0) { $accent = 238 }
            if ($step -eq 1) { $accent = 242 }
            $big = Big-Rows $WORDS[$cur]; $wl = Wrap-Text $TEXTS[$cur] $TW 3
            for ($i = 0; $i -lt 4; $i++) { Inner ($FP + 2 + $i) }
            for ($i = 0; $i -lt 3; $i++) { At ($FP + 2 + $i) ($PX + 3); [void]$SB.Append($BOLD + $CC[$accent] + $big[$i] + $RST) }
            At ($FP + 2) $TXC; [void]$SB.Append($BOLD + $CC[$tl[$step]] + $TITLES[$cur] + $RST)
            for ($i = 0; $i -lt 3; $i++) { At ($FP + 3 + $i) $TXC; [void]$SB.Append($CC[$bl[$step]] + $wl[$i] + $RST) }
        }
        function Show-Card([int]$cur) { foreach ($st in 0..3) { Card $cur $st; if (-not $script:FAST) { Nap 45 } }; Segments $cur }

        Panel $FP 7 'WHAT YOU CAN DO'
        Keys $KEYS1 '←→' 'browse' 'enter' 'continue'
        $script:FAST = $false; $card = 0; Show-Card 0; $idle = 0; $go = $false
        while (-not $go) {
            Nap 80
            Twinkle
            if (Poll-Key) {
                if ($script:KEY -eq 'right') { $card = ($card + 1) % $NCARD; Show-Card $card; $idle = 0 }
                elseif ($script:KEY -eq 'left') { $card = ($card + $NCARD - 1) % $NCARD; Show-Card $card; $idle = 0 }
                elseif ($script:KEY -eq 'enter') { $go = $true; continue }
                elseif ($script:KEY -eq 'q' -or $script:KEY -eq 'ctrlc') { Quit-Key }
            }
            $idle++
            if ($idle -ge 40) {        # about 3 s on a card, then the next one
                if ($card -eq $NCARD - 1) { Clear-Line $KEYS1; Keys $KEYS1 'enter' 'begin' '←→' 'browse again'; $idle = -999 }
                else { $card++; Show-Card $card; $idle = 0 }
            }
        }
    }

    # ------------------------------------------------------------ 6. finding the game
    for ($r = $MSG; $r -le $Y0 + $BLOCK - 1; $r++) { Clear-Line $r }
    # (the lower area is cleared after the prompt: YOUR SETUP does not cover the prompt panel's last rows)
    if (-not $script:GAME_ROOT) { Screen-NotFound ''; Clear-Lower }
    elseif (-not (Is-Game $script:GAME_ROOT)) { Screen-NotFound "That folder has no `"$EXE`" in it. Try again."; Clear-Lower }
    Set-ShortPath
    if (Is-Demo $script:GAME_ROOT) { Screen-Demo }
    Refresh-State
    Panel $SP 6 'YOUR SETUP'
    $script:FAST = $false
    for ($f = 0; $f -lt 12; $f++) {
        Inner ($SP + 1)
        [void]$SB.Append($CC[247] + 'Game'.PadRight(12) + $RST + $CC[218] + $SPINNER[$f % $SPINNER.Count] + $RST + '  ' + $CC[248] + 'looking in your Steam libraries…' + $RST)
        Twinkle; Frame 60
    }
    Draw-Setup

    # ------------------------------------------------------------ 7. the menu
    $script:MSEL = 0
    Menu-Meta
    Panel $MP ($MENU.Count + 2) 'WHAT WOULD YOU LIKE TO DO'
    for ($i = 0; $i -lt $MENU.Count; $i++) { Draw-Item $i; Frame 40 }
    Keys $KEYS2 '↑↓' 'move' 'enter' 'choose' 'q' 'quit'
    while ($true) {
        Nap 80; Twinkle
        if (-not (Poll-Key)) { continue }
        $pressed = $script:KEY
        if ($pressed -eq 'up') { $script:MSEL = ($script:MSEL + $MENU.Count - 1) % $MENU.Count; Draw-Menu }
        elseif ($pressed -eq 'down') { $script:MSEL = ($script:MSEL + 1) % $MENU.Count; Draw-Menu }
        elseif ($pressed -match '^[1-6]$') { $script:MSEL = [int]$pressed - 1; Draw-Menu }
        elseif ($pressed -eq 'q' -or $pressed -eq 'ctrlc') { Quit-Key }
        elseif ($pressed -eq 'esc') { Hint 'Choose Quit to leave (or press q twice)' }
        elseif ($pressed -eq 'enter') {
            if ($script:MSEL -eq 0) { Act-Install; Draw-Main }
            elseif ($script:MSEL -eq 1) { Act-Trace; Draw-Main }
            elseif ($script:MSEL -eq 2) { Act-Open; Draw-Main }
            elseif ($script:MSEL -eq 3) { if (Act-Uninstall) { Draw-Main } }
            elseif ($script:MSEL -eq 4) { if (Act-UninstallAll) { Draw-Main } }
            else { Farewell }
        }
    }
} catch {
    $FATAL = $_.Exception.Message
    try { [Console]::Error.WriteLine(($_ | Out-String)) } catch { }
    $EXIT_CODE = 1
} finally {
    if ($IN_UI) {
        try {
            Flush-Screen
            [Console]::Out.Write("$RST$ESC[?7h$ESC[?25h$ESC[?1049l")
        } catch { }
        if ($RESIZED) { try { [Console]::SetWindowSize($RESIZED[0], $RESIZED[1]) } catch { } }
        if ($SUMMARY.Count -gt 0) {
            try {
                [Console]::Out.WriteLine(''); [Console]::Out.WriteLine("CWY:LS Mod Loader installer $VERSION_UI")
                foreach ($l in $SUMMARY) { [Console]::Out.WriteLine("  - $l") }
                [Console]::Out.WriteLine('')
            } catch { }
        }
    }
    if ($FATAL) {
        try { [Console]::Out.WriteLine("ERROR: $FATAL") } catch { }
        Log-Line "error: $FATAL"
    }
    if ($null -ne $script:CTRLC_SAVED) { try { [Console]::TreatControlCAsInput = $script:CTRLC_SAVED } catch { } }
    if ($null -ne $script:OUT_MODE_SAVED) { try { [void][CwyLsInstaller.Con]::SetConsoleMode($script:H_OUT, $script:OUT_MODE_SAVED) } catch { } }
    if ($ENC_SAVED) { try { [Console]::Out.Flush(); [Console]::OutputEncoding = $ENC_SAVED } catch { } }
}
exit $EXIT_CODE
