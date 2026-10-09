[CmdletBinding()]
param([Parameter(Mandatory)][ValidateSet('Status','Check','Preflight','Unlock','Verify')][string]$Mode,[Guid]$RunId=[Guid]::NewGuid())
$ErrorActionPreference='Stop'
# All hardware work goes through the configured resident backend.
$command=if($Mode -eq 'Preflight'){'Environment'}else{$Mode}
$background=Join-Path $PSScriptRoot 'build\CMP90HXBackground.exe'
if(!(Test-Path -LiteralPath $background)){throw 'Compile Console with prepared backends first.'}
& $background --request $command
exit $LASTEXITCODE