# Builds Release and packages release/Troubleshootr-<version>.zip (app + CLI, no .pdb files).
# Usage: powershell -NoProfile -File scripts/package.ps1
# Then: git tag v<version>; git push origin v<version>; gh release create v<version> release/Troubleshootr-<version>.zip --prerelease
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$version = ([xml](Get-Content "$root\Directory.Build.props")).Project.PropertyGroup.Version
& "C:\Program Files\dotnet\dotnet.exe" build "$root\Troubleshootr.sln" -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "build failed" }
$out = "$root\release\Troubleshootr-$version"
Remove-Item -Recurse -Force $out, "$out.zip" -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $out | Out-Null
Copy-Item "$root\src\Troubleshootr.App\bin\Release\net472\*" $out -Exclude *.pdb, *.log -Recurse  # includes lhm\x64 and lhm\x86 (sensor library)
Copy-Item "$root\src\Troubleshootr.Cli\bin\Release\net472\troubleshootr-cli.exe*" $out
Compress-Archive -Path $out -DestinationPath "$out.zip"
"{0}: {1:N1} MB unzipped, {2:N1} MB zip" -f $version, ((Get-ChildItem $out -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), ((Get-Item "$out.zip").Length / 1MB)
