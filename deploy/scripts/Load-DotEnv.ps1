#Requires -Version 5.1
<#
.SYNOPSIS
  从项目根 .env 加载环境变量（不打印值），用于迁移与本地启动。
#>
param(
    [string]$EnvFile = (Join-Path $PSScriptRoot "..\..\.env")
)

$EnvFile = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\..\.env"))
if (-not (Test-Path $EnvFile)) {
    Write-Error "缺少 .env，请复制 .env.example 为 .env 并填写 POSTGRES_PASSWORD / ConnectionStrings__DefaultConnection"
    exit 1
}

Get-Content $EnvFile | ForEach-Object {
    $line = $_.Trim()
    if ([string]::IsNullOrWhiteSpace($line) -or $line.StartsWith("#")) { return }
    $idx = $line.IndexOf("=")
    if ($idx -lt 1) { return }
    $name = $line.Substring(0, $idx).Trim()
    $value = $line.Substring($idx + 1).Trim()
    Set-Item -Path "Env:$name" -Value $value
}

Write-Host "Loaded environment from .env (values not shown)."
