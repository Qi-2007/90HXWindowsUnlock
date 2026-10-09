[CmdletBinding()]
param([string]$MSBuild,[string]$OutputDirectory)
$ErrorActionPreference='Stop'
# Historical alias: import a prepared backend into backends/native first.
& (Join-Path (Split-Path -Parent $PSScriptRoot) 'build-gui.ps1') -MSBuild $MSBuild -OutputDirectory $OutputDirectory -NonInteractive