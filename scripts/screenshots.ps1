# Regenerates docs\screenshots\*.png from sample notes in a temporary folder (your real notes are never touched).
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $root 'src\Notes\bin\Release\net10.0-windows\Notes.exe'
dotnet build (Join-Path $root 'src\Notes\Notes.csproj') -c Release | Out-Null
if ($LASTEXITCODE) { throw 'Build failed' }
$out = Join-Path $root 'docs\screenshots'
New-Item -ItemType Directory -Force $out | Out-Null

function New-SampleData([string]$dir, [hashtable]$settings) {
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
    $notes = Join-Path $dir 'notes'
    $samples = @(
        @{ Cat = 'Work';     Name = 'Launch checklist.md'; Age = 0; Body = @"
# Launch checklist

- [x] Final build signed
- [x] Installer tested on a clean PC
- [ ] Write release notes
- [ ] Record a 30-second demo

**Friday:** ship it, then celebrate.
"@ },
        @{ Cat = 'Ideas';    Name = 'Weekend project.md'; Age = 1; Body = "A tiny weather widget that lives in the tray.`nShows rain in the next hour, nothing else." },
        @{ Cat = 'General';  Name = 'Groceries.txt';     Age = 2; Body = "Oat milk`nCoffee beans`nLemons`nBasil" },
        @{ Cat = 'Work';     Name = 'Meeting notes.md';  Age = 3; Body = "## Design sync`nKeep the toolbar minimal. Move export into the note menu." },
        @{ Cat = 'Personal'; Name = 'Book list.md';      Age = 5; Body = "- The Pragmatic Programmer`n- Project Hail Mary`n- Dune" }
    )
    foreach ($s in $samples) {
        $d = Join-Path $notes $s.Cat
        New-Item -ItemType Directory -Force $d | Out-Null
        $f = Join-Path $d $s.Name
        Set-Content -Path $f -Value $s.Body -Encoding utf8 -NoNewline
        (Get-Item $f).LastWriteTime = (Get-Date).AddDays(-$s.Age).AddMinutes(-$s.Age * 17)
    }
    # A protected note: only the list entry is shown, so placeholder bytes with the file header are enough.
    $locked = Join-Path $notes 'Personal\Passwords.md.locked'
    [IO.File]::WriteAllBytes($locked, [Text.Encoding]::ASCII.GetBytes('NOTESLK1' + ('x' * 64)))
    (Get-Item $locked).LastWriteTime = (Get-Date).AddDays(-4)
    $settings | ConvertTo-Json | Set-Content (Join-Path $dir 'settings.json') -Encoding utf8
}

function Capture([string]$name, [hashtable]$settings) {
    $dir = Join-Path $env:TEMP "notes-screenshot-$name"
    New-SampleData $dir $settings
    $env:NOTES_DATA_DIR = $dir
    $env:NOTES_SCREENSHOT = Join-Path $out "$name.png"
    $p = Start-Process $exe -PassThru
    if (-not $p.WaitForExit(20000)) { $p.Kill(); throw "Screenshot '$name' timed out" }
    Remove-Item Env:NOTES_DATA_DIR, Env:NOTES_SCREENSHOT
    Remove-Item $dir -Recurse -Force
    Write-Host "Saved docs\screenshots\$name.png"
}

Capture 'light' @{ Mode = 'Light'; Accent = 'Honey'; WinX = 200; WinY = 200; WinW = 780; WinH = 500 }
Capture 'dark-slim' @{ Mode = 'Dark'; Accent = 'Lavender'; WinX = 200; WinY = 100; WinW = 260; WinH = 620 }
