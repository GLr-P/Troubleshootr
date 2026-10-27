# Builds Release and packages:
#   release/Troubleshootr.exe            the single exe (every DLL embedded; the main download)
#   release/Troubleshootr-<version>.zip  the app folder plus troubleshootr-cli.exe (no .pdb files)
# Usage: powershell -NoProfile -File scripts/package.ps1
# Then: git tag v<version>; git push origin v<version>; gh release create v<version> release/Troubleshootr.exe release/Troubleshootr-<version>.zip
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$version = ([xml](Get-Content "$root\Directory.Build.props")).Project.PropertyGroup.Version
& "C:\Program Files\dotnet\dotnet.exe" build "$root\Troubleshootr.sln" -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "build failed" }
$bin = "$root\src\Troubleshootr.App\bin\Release\net472"
$out = "$root\release\Troubleshootr-$version"
Remove-Item -Recurse -Force $out, "$out.zip", "$root\release\Troubleshootr.exe" -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $out | Out-Null
Copy-Item "$bin\*" $out -Exclude *.pdb, *.log -Recurse  # includes lhm\x64, x86, arm64 (sensor library)
Copy-Item "$root\src\Troubleshootr.Cli\bin\Release\net472\troubleshootr-cli.exe*" $out
Compress-Archive -Path $out -DestinationPath "$out.zip"
Copy-Item "$bin\Troubleshootr.exe" "$root\release\Troubleshootr.exe"

# The lone exe must work by itself: scan, report and sensor library from an empty folder.
$lone = Join-Path $env:TEMP "troubleshootr-package-check"
Remove-Item -Recurse -Force $lone -ErrorAction SilentlyContinue
New-Item -ItemType Directory $lone | Out-Null
Copy-Item "$root\release\Troubleshootr.exe" $lone
$p = Start-Process "$lone\Troubleshootr.exe" -ArgumentList '--selftest', '--out', "$lone\result.txt" -Wait -PassThru
Get-Content "$lone\result.txt"
if ($p.ExitCode -ne 0) { throw "single exe self-test failed" }

$exeMB = (Get-Item "$root\release\Troubleshootr.exe").Length / 1MB
if ($exeMB -gt 15) { throw ("single exe is {0:N1} MB, over the 15 MB budget" -f $exeMB) }
"{0}: single exe {1:N1} MB; folder {2:N1} MB unzipped, {3:N1} MB zip" -f $version, $exeMB, ((Get-ChildItem $out -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), ((Get-Item "$out.zip").Length / 1MB)
