# Match native UTF-8 output (MSBuild and the worker) to Windows PowerShell's decoder.
$taskConsoleUtf8=New-Object Text.UTF8Encoding($false)
[Console]::InputEncoding=$taskConsoleUtf8
[Console]::OutputEncoding=$taskConsoleUtf8
$OutputEncoding=$taskConsoleUtf8
