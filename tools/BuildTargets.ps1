# -----------------------------------------------------------------------------
# Agents Hub - Multi-Target Cross-Platform Build & Package Engine
# -----------------------------------------------------------------------------
[CmdletBinding()]
param(
    [string[]]$Targets = @("win-x64", "linux-x64", "linux-arm64", "osx-arm64", "osx-x64"),
    [string]$Version,
    [string]$OutputDir,
    [switch]$SkipInno
)

$ErrorActionPreference = "Stop"

# Normalize Targets: whether passed as an array, comma-separated string, or space-separated items
$normalizedTargets = @()
if ($Targets) {
    foreach ($item in $Targets) {
        if ($item) {
            $parts = $item -split '[\s,]+'
            foreach ($p in $parts) {
                $trimmed = $p.Trim()
                if ($trimmed) { $normalizedTargets += $trimmed }
            }
        }
    }
}
if ($normalizedTargets.Count -gt 0) {
    $Targets = $normalizedTargets
} else {
    $Targets = @("win-x64", "linux-x64", "linux-arm64", "osx-arm64", "osx-x64")
}

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$rootDir = (Get-Item "$scriptDir\..").FullName
Set-Location $rootDir

if (-not $OutputDir) {
    $OutputDir = Join-Path $rootDir "dist"
}
if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}

# Resolve version
if (-not $Version) {
    $csprojPath = Join-Path $rootDir "AgentsHub.csproj"
    if (Test-Path $csprojPath) {
        $xml = [xml](Get-Content $csprojPath)
        $node = $xml.SelectSingleNode("//Version")
        if ($node) {
            $Version = $node.InnerText.Trim()
        }
    }
    if (-not $Version) { $Version = "1.0.5" }
}

Write-Host "========================================================" -ForegroundColor Cyan
Write-Host " Agents Hub Multi-Target Release Builder" -ForegroundColor Cyan
Write-Host " Version: $Version" -ForegroundColor Yellow
Write-Host " Targets: $($Targets -join ', ')" -ForegroundColor Yellow
Write-Host " Output:  $OutputDir" -ForegroundColor Yellow
Write-Host "========================================================" -ForegroundColor Cyan

$tempBase = Join-Path $rootDir "bin\Release\staging"
if (Test-Path $tempBase) {
    Remove-Item $tempBase -Recurse -Force -ErrorAction SilentlyContinue
}
New-Item -ItemType Directory -Path $tempBase -Force | Out-Null

$results = @{}

foreach ($target in $Targets) {
    Write-Host "`n[*] Building target: $target..." -ForegroundColor Green
    $stagingDir = Join-Path $tempBase $target
    New-Item -ItemType Directory -Path $stagingDir -Force | Out-Null

    switch ($target) {
        "win-x64" {
            # 1. Publish net10.0-windows
            $publishDir = Join-Path $rootDir "bin\Release\net10.0-windows\win-x64\publish"
            if (Test-Path $publishDir) {
                Remove-Item $publishDir -Recurse -Force -ErrorAction SilentlyContinue
            }
            New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

            Write-Host "  -> Publishing single-file binary (win-x64)..." -ForegroundColor Gray
            & dotnet publish "$rootDir\AgentsHub.csproj" -f net10.0-windows -c Release -r win-x64 --self-contained -o $publishDir
            if ($LASTEXITCODE -ne 0) { throw "Dotnet publish failed for win-x64" }

            # Per-profile OAuth tokens belong to the machine that ran the app. A publish folder
            # that was used as a run directory can carry them, so they never enter a package.
            Remove-Item (Join-Path $publishDir "Profiles") -Recurse -Force -ErrorAction SilentlyContinue

            # Copy to staging for zip packaging
            Copy-Item "$publishDir\*" $stagingDir -Recurse -Force
            Remove-Item (Join-Path $stagingDir "Profiles") -Recurse -Force -ErrorAction SilentlyContinue

            # 2. Package Portable Zip
            $zipPath = Join-Path $OutputDir "AgentsHub-v$Version-win-x64.zip"
            if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
            Write-Host "  -> Packaging portable zip: $zipPath..." -ForegroundColor Gray
            & tar.exe -a -c -f $zipPath -C $stagingDir *
            $results["win-x64-zip"] = $zipPath

            # 3. Compile Inno Setup Installer
            if (-not $SkipInno) {
                $iscc = ""
                if (Test-Path "C:\Program Files (x86)\Inno Setup 6\ISCC.exe") {
                    $iscc = "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
                } elseif (Test-Path "C:\Program Files\Inno Setup 6\ISCC.exe") {
                    $iscc = "C:\Program Files\Inno Setup 6\ISCC.exe"
                }

                if ($iscc) {
                    Write-Host "  -> Compiling Inno Setup installer..." -ForegroundColor Gray
                    & $iscc "/DMyAppVersion=$Version" "/DMyAppSourceDir=$publishDir" "$rootDir\AgentsHub.iss"
                    if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed" }
                    $installerPath = Join-Path $OutputDir "AgentsHub-Setup-v$Version.exe"
                    $results["win-x64-setup"] = $installerPath
                } else {
                    Write-Host "  [!] Inno Setup not found. Skipping installer exe creation." -ForegroundColor Yellow
                }
            }
        }

        "linux-x64" {
            Write-Host "  -> Publishing self-contained binary (linux-x64)..." -ForegroundColor Gray
            & dotnet publish "$rootDir\AgentsHub.csproj" -f net10.0 -c Release -r linux-x64 --self-contained -o $stagingDir
            if ($LASTEXITCODE -ne 0) { throw "Dotnet publish failed for linux-x64" }

            Copy-Item "$rootDir\LICENSE" $stagingDir -ErrorAction SilentlyContinue
            Copy-Item "$rootDir\README.md" $stagingDir -ErrorAction SilentlyContinue

            $tarPath = Join-Path $OutputDir "AgentsHub-v$Version-linux-x64.tar.gz"
            if (Test-Path $tarPath) { Remove-Item $tarPath -Force }
            Write-Host "  -> Packaging tarball: $tarPath..." -ForegroundColor Gray
            & tar.exe -czf $tarPath -C $stagingDir .
            $results["linux-x64"] = $tarPath
        }

        "linux-arm64" {
            Write-Host "  -> Publishing self-contained binary (linux-arm64)..." -ForegroundColor Gray
            & dotnet publish "$rootDir\AgentsHub.csproj" -f net10.0 -c Release -r linux-arm64 --self-contained -o $stagingDir
            if ($LASTEXITCODE -ne 0) { throw "Dotnet publish failed for linux-arm64" }

            Copy-Item "$rootDir\LICENSE" $stagingDir -ErrorAction SilentlyContinue
            Copy-Item "$rootDir\README.md" $stagingDir -ErrorAction SilentlyContinue

            $tarPath = Join-Path $OutputDir "AgentsHub-v$Version-linux-arm64.tar.gz"
            if (Test-Path $tarPath) { Remove-Item $tarPath -Force }
            Write-Host "  -> Packaging tarball: $tarPath..." -ForegroundColor Gray
            & tar.exe -czf $tarPath -C $stagingDir .
            $results["linux-arm64"] = $tarPath
        }

        "osx-arm64" {
            Write-Host "  -> Publishing self-contained binary (osx-arm64 Apple Silicon)..." -ForegroundColor Gray
            & dotnet publish "$rootDir\AgentsHub.csproj" -f net10.0 -c Release -r osx-arm64 --self-contained -o $stagingDir
            if ($LASTEXITCODE -ne 0) { throw "Dotnet publish failed for osx-arm64" }

            Copy-Item "$rootDir\LICENSE" $stagingDir -ErrorAction SilentlyContinue
            Copy-Item "$rootDir\README.md" $stagingDir -ErrorAction SilentlyContinue

            $tarPath = Join-Path $OutputDir "AgentsHub-v$Version-osx-arm64.tar.gz"
            if (Test-Path $tarPath) { Remove-Item $tarPath -Force }
            Write-Host "  -> Packaging tarball: $tarPath..." -ForegroundColor Gray
            & tar.exe -czf $tarPath -C $stagingDir .
            $results["osx-arm64"] = $tarPath
        }

        "osx-x64" {
            Write-Host "  -> Publishing self-contained binary (osx-x64 Intel Mac)..." -ForegroundColor Gray
            & dotnet publish "$rootDir\AgentsHub.csproj" -f net10.0 -c Release -r osx-x64 --self-contained -o $stagingDir
            if ($LASTEXITCODE -ne 0) { throw "Dotnet publish failed for osx-x64" }

            Copy-Item "$rootDir\LICENSE" $stagingDir -ErrorAction SilentlyContinue
            Copy-Item "$rootDir\README.md" $stagingDir -ErrorAction SilentlyContinue

            $tarPath = Join-Path $OutputDir "AgentsHub-v$Version-osx-x64.tar.gz"
            if (Test-Path $tarPath) { Remove-Item $tarPath -Force }
            Write-Host "  -> Packaging tarball: $tarPath..." -ForegroundColor Gray
            & tar.exe -czf $tarPath -C $stagingDir .
            $results["osx-x64"] = $tarPath
        }

        Default {
            Write-Host "  [!] Unknown target '$target', skipping." -ForegroundColor Yellow
        }
    }
}

# Clean staging
Remove-Item $tempBase -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "`n========================================================" -ForegroundColor Cyan
Write-Host " SUCCESS: Built packages in $OutputDir" -ForegroundColor Green
foreach ($kv in $results.GetEnumerator()) {
    if (Test-Path $kv.Value) {
        $sizeMb = [Math]::Round((Get-Item $kv.Value).Length / 1MB, 2)
        Write-Host "  + $($kv.Key): $(Split-Path $kv.Value -Leaf) ($sizeMb MB)" -ForegroundColor White
    }
}
Write-Host "========================================================" -ForegroundColor Cyan
