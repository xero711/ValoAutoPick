$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$version = ([xml](Get-Content -LiteralPath (Join-Path $projectRoot 'src/ValoAutoPick/ValoAutoPick.csproj') -Raw)).Project.PropertyGroup.Version
$distDir = "dist/ValoAutoPick-$version-Win-x64"
$publishDir = "artifacts/publish-$version"
$zipPath = "dist/ValoAutoPick-$version-Win-x64.zip"
Push-Location -LiteralPath $projectRoot
try {
    dotnet run --project tests/ValoAutoPick.Tests.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    dotnet run --project tests/UiMotion/UiMotion.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'UI animation checks failed.' }
    dotnet publish src/ValoAutoPick/ValoAutoPick.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    New-Item -ItemType Directory -Path $distDir -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $publishDir 'ValoAutoPick.exe') -Destination (Join-Path $distDir 'ValoAutoPick.exe') -Force
    $packageRoot = Join-Path $env:USERPROFILE '.nuget\packages'
    $assetsPath = Join-Path $projectRoot 'src/ValoAutoPick/obj/project.assets.json'
    $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
    $runtimePackage = $assets.project.frameworks.PSObject.Properties.Value.downloadDependencies |
        Where-Object { $_.name -ieq 'Microsoft.NETCore.App.Runtime.win-x64' } | Select-Object -First 1
    if (-not $runtimePackage) { throw 'Runtime licenses were not restored.' }
    $runtimeVersion = $runtimePackage.version.Trim('[', ']').Split(',')[0].Trim()
    $licenses = @{
        'microsoft.web.webview2\1.0.4314.35\LICENSE.txt' = 'WebView2-LICENSE.txt'
        "microsoft.netcore.app.runtime.win-x64\$runtimeVersion\LICENSE.TXT" = 'DOTNET-LICENSE.txt'
        "microsoft.netcore.app.runtime.win-x64\$runtimeVersion\THIRD-PARTY-NOTICES.TXT" = 'DOTNET-THIRD-PARTY-NOTICES.txt'
    }
    foreach ($entry in $licenses.GetEnumerator()) {
        Copy-Item -LiteralPath (Join-Path $packageRoot $entry.Key) -Destination (Join-Path $distDir $entry.Value) -Force
    }
    $releaseFiles = @('ValoAutoPick.exe') + @($licenses.Values)
    $releasePaths = @($releaseFiles | ForEach-Object { Join-Path $distDir $_ })
    Compress-Archive -LiteralPath $releasePaths -DestinationPath $zipPath -Force
    Write-Host "Ready: $distDir/ValoAutoPick.exe"
}
finally { Pop-Location }
