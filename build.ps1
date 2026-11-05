$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
Push-Location -LiteralPath $projectRoot
try {
    dotnet run --project tests/ValoAutoPick.Tests.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    dotnet publish src/ValoAutoPick/ValoAutoPick.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o dist/ValoAutoPick-smooth-corners
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    Copy-Item -LiteralPath README.md -Destination dist/ValoAutoPick-smooth-corners/README.md -Force
    Copy-Item -LiteralPath THIRD-PARTY-NOTICES.md -Destination dist/ValoAutoPick-smooth-corners/THIRD-PARTY-NOTICES.md -Force
    Copy-Item -LiteralPath analysis/APK_ANALYSIS.md -Destination dist/ValoAutoPick-smooth-corners/APK_ANALYSIS.md -Force
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
        Copy-Item -LiteralPath (Join-Path $packageRoot $entry.Key) -Destination (Join-Path 'dist/ValoAutoPick-smooth-corners' $entry.Value) -Force
    }
    Compress-Archive -Path dist/ValoAutoPick-smooth-corners/* -DestinationPath dist/ValoAutoPick-smooth-corners-Win11-x64.zip -Force
    Write-Host 'Ready: dist/ValoAutoPick-smooth-corners/ValoAutoPick.exe'
}
finally { Pop-Location }
