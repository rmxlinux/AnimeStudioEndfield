param(
    [string]$AclSource = (Join-Path $PSScriptRoot '.deps\acl'),
    [string]$Compiler = 'C:\ProgramData\mingw64\mingw64\bin\g++.exe'
)

$ErrorActionPreference = 'Stop'
$aclCommit = '3ee568542eca4428e1041908b4a644b98e7885bd'

if (-not (Test-Path -LiteralPath $AclSource)) {
    git clone https://github.com/nfrechette/acl.git $AclSource
    if ($LASTEXITCODE -ne 0) { throw 'ACL clone failed.' }
    git -C $AclSource checkout $aclCommit
    if ($LASTEXITCODE -ne 0) { throw 'ACL checkout failed.' }
    git -C $AclSource submodule update --init --recursive
    if ($LASTEXITCODE -ne 0) { throw 'ACL submodule update failed.' }
}

$actualCommit = git -C $AclSource rev-parse HEAD
if ($actualCommit -ne $aclCommit) { throw "Expected ACL commit $aclCommit, got $actualCommit." }
if (-not (Test-Path -LiteralPath (Join-Path $AclSource 'external\rtm\includes\rtm\quatf.h'))) {
    throw 'ACL RTM submodule is missing. Run git submodule update --init --recursive.'
}
if (-not (Test-Path -LiteralPath $Compiler)) { throw "C++ compiler not found: $Compiler" }

$env:PATH = (Split-Path -Parent $Compiler) + ';' + $env:PATH
$output = Join-Path $PSScriptRoot '..\..\AnimeStudio.Libraries\x64\endfieldacl.dll'
& $Compiler -shared -O2 -std=c++17 -static -static-libgcc -static-libstdc++ `
    "-I$(Join-Path $AclSource 'includes')" `
    "-I$(Join-Path $AclSource 'external\rtm\includes')" `
    (Join-Path $PSScriptRoot 'EndfieldACL.cpp') -o $output
if ($LASTEXITCODE -ne 0) { throw 'Endfield ACL native build failed.' }
Write-Output $output
