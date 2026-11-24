param(
    [string]$OutputRoot = (Join-Path $PSScriptRoot 'dist-endfield-acl')
)

$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath $OutputRoot) {
    throw "Output directory already exists: $OutputRoot"
}

$outputPath = [IO.Path]::GetFullPath($OutputRoot)
dotnet build (Join-Path $PSScriptRoot 'AnimeStudio.Patcher') -c Release -f net9.0
if ($LASTEXITCODE -ne 0) { throw 'Patcher build failed.' }
$patcher = Join-Path $PSScriptRoot 'AnimeStudio.Patcher/bin/Release/net9.0/AnimeStudio.Patcher.exe'

foreach ($tfm in 'net8.0-windows', 'net9.0-windows') {
    foreach ($project in 'AnimeStudio.CLI', 'AnimeStudio.GUI') {
        dotnet build (Join-Path $PSScriptRoot $project) -c Release -f $tfm --no-incremental "-p:SolutionDir=$PSScriptRoot/"
        if ($LASTEXITCODE -ne 0) { throw "$project $tfm build failed." }

        $exe = Join-Path $PSScriptRoot "$project/bin/Release/$tfm/$project.exe"
        & $patcher $exe -d bin
        if ($LASTEXITCODE -ne 0) { throw "$project $tfm patch failed." }
    }

    $bin = Join-Path $outputPath "$tfm/bin"
    New-Item -ItemType Directory -Path $bin -Force | Out-Null
    Copy-Item (Join-Path $PSScriptRoot "AnimeStudio.CLI/bin/Release/$tfm/*") $bin -Recurse -Force
    Copy-Item (Join-Path $PSScriptRoot "AnimeStudio.GUI/bin/Release/$tfm/*") $bin -Recurse -Force

    foreach ($name in 'AnimeStudio.CLI.exe', 'AnimeStudio.GUI.exe', 'LICENSE', 'ACL-LICENSE.txt') {
        Move-Item (Join-Path $bin $name) (Join-Path $outputPath $tfm) -Force
    }
    foreach ($name in 'endfieldacl.dll', 'AnimeStudio.FBXNative.dll') {
        if (-not (Test-Path -LiteralPath (Join-Path $bin "x64/$name"))) {
            throw "Package is missing x64/$name for $tfm."
        }
    }
}

Write-Output $outputPath
