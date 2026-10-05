[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Status','Check','Preflight','Unlock','Verify')][string]$Mode,
    [Guid]$RunId=[Guid]::NewGuid()
)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'tools\ConsoleEncoding.ps1')
[Console]::OutputEncoding=New-Object System.Text.UTF8Encoding($false)
$root=$PSScriptRoot
$logs=Join-Path $root ('logs\gui\'+$RunId.ToString('N'))
New-Item -ItemType Directory -Path $logs -Force | Out-Null
$guiLog=Join-Path $logs 'gui-workflow.log'
function Write-GuiLog([string]$line) {
    $message='{0} {1}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss.fff'),$line
    Add-Content -LiteralPath $guiLog -Value $message -Encoding UTF8
    Write-Host $message
}
try {
    Write-GuiLog "GUI_WORKFLOW_BEGIN mode=$Mode"
    if ($Mode -ne 'Check') {
        $admin=([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
        if (!$admin) { throw 'This workflow requires administrator elevation.' }
        if ($Mode -ne 'Status') {
        $service=Get-Service -Name CMP90HXDma -ErrorAction SilentlyContinue
        if (!$service) { throw 'DMA_DRIVER_NOT_INSTALLED: install CMP90HXDma with install-dma-driver.ps1 first.' }
        if ($service.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Running) {
            Write-GuiLog 'Starting CMP90HXDma for this boot.'
            & sc.exe start CMP90HXDma | Out-Host
            if ($LASTEXITCODE -ne 0) { throw "CMP90HXDma start failed: $LASTEXITCODE" }
            $service.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Running,[TimeSpan]::FromSeconds(10))
        }
    }
    }
    if ($Mode -eq 'Check') {
        Start-Transcript -Path (Join-Path $logs 'check.log') | Out-Null
        try {
            & (Join-Path $root 'run-full-test.ps1') -Mode Preflight -PhysicalDmaExperiment -ValidateOnly
        } finally { Stop-Transcript | Out-Null }
    } else {
        $conservative=$Mode -eq 'Unlock' -or $Mode -eq 'Verify'
        & (Join-Path $root 'run-full-test.ps1') -Mode $Mode -PhysicalDmaExperiment -Conservative:$conservative -LogDirectory $logs
    }
    $code=$LASTEXITCODE
    Write-GuiLog "GUI_WORKFLOW_END mode=$Mode exit=$code"
    exit $code
} catch {
    Write-GuiLog "GUI_WORKFLOW_FAILED mode=$Mode error=$($_.Exception.Message)"
    exit 1
}

