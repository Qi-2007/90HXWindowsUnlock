$ErrorActionPreference='Stop'
Add-Type -TypeDefinition (Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\ui\UnlockSnapshot.cs') -Raw -Encoding UTF8) -ReferencedAssemblies System.Web.Extensions,System.Core
$json='{"Schema":1,"DeviceId":571281630,"Gpu":{"Status":258},"Bridge":{"Status":258},"Registers":[{"Offset":8534044,"Value":2290649224},{"Offset":8534048,"Value":8},{"Offset":8534064,"Value":4}]}'
$sample=[CMP90HX.Control.UnlockSnapshot]::Parse($json)
if ([CMP90HX.Control.UnlockSnapshot]::DescribeLink($sample.Gpu) -ne 'Gen2 ×16') { throw 'Link decoding failed' }
if ($sample.FunctionState(@(0x82381c, [uint32]2290649224, 0x823820, 8)) -ne '已解锁') { throw 'Compute decoding failed' }
if ($sample.FunctionState(@(0x823830, 4)) -ne '已解锁') { throw 'Graphics decoding failed' }
$sample.Registers[2].Value=0
if ($sample.FunctionState(@(0x823830, 4)) -ne '未达解锁值') { throw 'Locked value incorrectly accepted' }
$sample.Registers[0].Error='unavailable'
if ($sample.FunctionState(@(0x82381c, [uint32]2290649224, 0x823820, 8)) -ne '无法确认') { throw 'Read error incorrectly accepted' }
$sample.Registers=@()
if ($sample.FunctionState(@(0x823830, 4)) -ne '无法确认') { throw 'Missing field incorrectly accepted' }
$sample.Gpu.Status=65535
if ([CMP90HX.Control.UnlockSnapshot]::DescribeLink($sample.Gpu) -ne '无法读取') { throw 'All-ones link incorrectly accepted' }
Write-Host 'GUI_STATUS_TESTS_PASSED (synthetic data only)'
