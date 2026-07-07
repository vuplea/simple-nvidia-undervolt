<#
Generates the per-card-range profile shortcut tree from profiles.json (the source of truth for
the profile values; PROFILES.md documents them and must be kept in step by hand). The publish
workflow runs this to build the release's profiles zip; a local run writes to profiles/out.

Each .lnk targets the installed windowless copy in Program Files (see Persistence.cs) and bakes
the profile's command line as its arguments.
#>
param(
    [string] $OutDir = (Join-Path $PSScriptRoot 'out')
)

$ErrorActionPreference = 'Stop'

$productName = 'simple-nvidia-undervolt'
$target = Join-Path $env:ProgramFiles "$productName\$productName-nocmd.exe"

$profiles = Get-Content (Join-Path $PSScriptRoot 'profiles.json') -Raw | ConvertFrom-Json
$shell = New-Object -ComObject WScript.Shell
$count = 0
foreach ($generation in $profiles.PSObject.Properties) {
    $dir = Join-Path $OutDir $generation.Name
    New-Item -ItemType Directory -Force $dir | Out-Null
    foreach ($profile in $generation.Value.PSObject.Properties) {
        $lnk = $shell.CreateShortcut((Join-Path $dir "$($profile.Name).lnk"))
        $lnk.TargetPath = $target
        $lnk.Arguments = $profile.Value
        $lnk.Description = "$productName $($profile.Value)"
        $lnk.Save()
        $count++
    }
}

Write-Output "Wrote $count shortcuts under $OutDir"
