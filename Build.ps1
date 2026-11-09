#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
$framework = if ([Environment]::Is64BitOperatingSystem) { 'Framework64' } else { 'Framework' }
$compiler = Join-Path $env:SystemRoot "Microsoft.NET\$framework\v4.0.30319\csc.exe"
if (!(Test-Path -LiteralPath $compiler -PathType Leaf)) { throw 'The .NET Framework C# compiler was not found.' }

$outputDirectory = Join-Path $PSScriptRoot 'dist'
[void][IO.Directory]::CreateDirectory($outputDirectory)
$output = Join-Path $outputDirectory 'ChatGPT-Desktop-Updater.exe'
$source = Join-Path $PSScriptRoot 'Launcher.cs'
$script = Join-Path $PSScriptRoot 'Install-ChatGPT.ps1'
& $compiler /nologo /target:exe /platform:anycpu /optimize+ "/out:$output" "/resource:$script,Install-ChatGPT.ps1" $source
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }
Write-Output $output
