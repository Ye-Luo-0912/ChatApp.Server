#Requires -Version 5.1
# 加载 .env 后对 UserDbContext 执行 database update
$ErrorActionPreference = "Stop"
$Root = Resolve-Path (Join-Path $PSScriptRoot "..\..")
Set-Location $Root
. (Join-Path $PSScriptRoot "Load-DotEnv.ps1")

if (-not $env:ConnectionStrings__DefaultConnection) {
    Write-Error "ConnectionStrings__DefaultConnection 未设置"
    exit 1
}

dotnet ef database update `
    --project Infrastructure `
    --startup-project Infrastructure `
    --context UserDbContext
