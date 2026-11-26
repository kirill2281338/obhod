# Сборка «обход»: программа + установщик.
# Пример: .\build.ps1 -ZapretDir "C:\path\to\zapret-discord-youtube-1.10.4"
param(
    [Parameter(Mandatory = $true)][string]$ZapretDir
)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$ZapretDir = (Resolve-Path -LiteralPath $ZapretDir).ProviderPath
if (-not (Test-Path -LiteralPath (Join-Path $ZapretDir 'bin\winws.exe') -PathType Leaf)) {
    throw 'Не найден bin\winws.exe в указанном комплекте Flowseal.'
}

# 1. программа (нужна .NET 10 Desktop Runtime на компьютере пользователя; установщик докачает её сам)
Push-Location "$root\src\app"
try {
    dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=none -o "$root\publish\app"
    if ($LASTEXITCODE -ne 0) { throw "Сборка приложения завершилась с кодом $LASTEXITCODE." }
} finally { Pop-Location }

# 2. содержимое установщика: app\obhod.exe и data\zapret\ (без результатов тестов)
$stage = "$root\publish\payload"
if (Test-Path -LiteralPath $stage) {
    $resolvedStage = Get-Item -LiteralPath $stage -Force
    $publish = Get-Item -LiteralPath (Join-Path $root 'publish') -Force
    if ($resolvedStage.FullName -ne $stage -or ($resolvedStage.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
        ($publish.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Небезопасный путь к каталогу сборки.' }
    Remove-Item -LiteralPath $resolvedStage.FullName -Recurse -Force
}
New-Item -ItemType Directory -Force "$stage\app", "$stage\data\zapret" | Out-Null
Copy-Item "$root\publish\app\obhod.exe" "$stage\app\"
robocopy $ZapretDir "$stage\data\zapret" /E /XD "test results" /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Копирование комплекта Flowseal завершилось с кодом $LASTEXITCODE." }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = "$root\src\Setup\payload.zip"
if (Test-Path $zip) { Remove-Item $zip }
[IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)

# 3. установщик (.NET Framework 4.8 — есть в любой Windows 10/11)
Push-Location "$root\src\Setup"
try {
    dotnet build -c Release -o "$root\publish\setup"
    if ($LASTEXITCODE -ne 0) { throw "Сборка установщика завершилась с кодом $LASTEXITCODE." }
} finally { Pop-Location }

$exe = "$root\publish\setup\obhod-setup.exe"
"готово: $exe"
"sha-256: " + (Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant()
