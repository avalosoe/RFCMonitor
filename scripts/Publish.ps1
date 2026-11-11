param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$output = Join-Path $root 'artifacts/publish'
$projects = @{ Api='RFCMonitor/RFCMonitor.csproj'; Service='RFCMonitor.Service/RFCMonitor.Service.csproj'; Manager='RFCMonitor.Agent.Manager/RFCMonitor.Agent.Manager.csproj' }
foreach ($name in @('Api','Service','Manager')) {
    $project = Join-Path $root $projects[$name]
    & dotnet publish $project -c $Configuration -m:1 --no-restore --self-contained false -o (Join-Path $output $name)
    if ($LASTEXITCODE -ne 0) { throw "Falló publicación de $name" }
}
$databaseOutput = Join-Path $output 'database'
if (Test-Path -LiteralPath $databaseOutput) { Remove-Item -LiteralPath $databaseOutput -Recurse -Force }
New-Item -ItemType Directory -Force $databaseOutput | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $output
Get-ChildItem (Join-Path $root 'database') -File | Copy-Item -Destination $databaseOutput
foreach ($name in @('Api','Service','Manager')) {
    Compress-Archive -Path (Join-Path $output "$name/*") -DestinationPath (Join-Path $output "$name.zip") -Force
}
Write-Output "Paquetes generados en $output. No se instala ni se inicia ningún servicio."
