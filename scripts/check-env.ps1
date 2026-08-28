# Verifies the abbctl build/run prerequisites and reports what is missing.
# Exit code 0 = ready to build; 1 = something is missing.

$ok = $true

Write-Host "abbctl environment check"
Write-Host "------------------------"

# 1. Windows
if ([System.Environment]::OSVersion.Platform -ne "Win32NT") {
    Write-Host "[FAIL] Windows required (the ABB PC SDK is Windows-only)."
    exit 1
}
Write-Host "[ ok ] Windows $([System.Environment]::OSVersion.Version)"

# 2. PC SDK DLLs (same probe order as the .csproj)
$probes = @()
if ($env:ABB_PCSDK_DIR) { $probes += $env:ABB_PCSDK_DIR }
$probes += @(
    "C:\Program Files (x86)\ABB\SDK\PCSDK\net48",
    "C:\Program Files (x86)\ABB\RobotStudio 2026\Bin-net48",
    "C:\Program Files (x86)\ABB\RobotStudio 2025\Bin",
    "C:\Program Files (x86)\ABB\RobotStudio 2024\Bin"
)
$sdkDir = $null
foreach ($p in $probes) {
    if (Test-Path (Join-Path $p "ABB.Robotics.Controllers.PC.dll")) { $sdkDir = $p; break }
}
if ($sdkDir) {
    Write-Host "[ ok ] PC SDK found: $sdkDir"
} else {
    Write-Host "[FAIL] PC SDK not found. Install RobotStudio 2024+ (any edition) or set"
    Write-Host "       ABB_PCSDK_DIR to a folder containing ABB.Robotics.Controllers.PC.dll (net48)."
    Write-Host "       Download: https://www.abb.com/global/en/areas/robotics/downloads"
    $ok = $false
}

# 3. .NET SDK
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet -and (Test-Path "C:\Program Files\dotnet\dotnet.exe")) {
    $dotnet = @{ Source = "C:\Program Files\dotnet\dotnet.exe" }
}
if ($dotnet) {
    $sdks = & $dotnet.Source --list-sdks 2>$null
    if ($sdks) {
        Write-Host "[ ok ] .NET SDK: $(($sdks | Select-Object -Last 1) -replace '\s*\[.*','')  ($($dotnet.Source))"
    } else {
        Write-Host "[FAIL] dotnet found but no SDK installed (runtime only)."
        Write-Host "       Install: winget install Microsoft.DotNet.SDK.10 --silent --accept-package-agreements --accept-source-agreements"
        $ok = $false
    }
} else {
    Write-Host "[FAIL] .NET SDK not found."
    Write-Host "       Install: winget install Microsoft.DotNet.SDK.10 --silent --accept-package-agreements --accept-source-agreements"
    $ok = $false
}

Write-Host "------------------------"
if ($ok) {
    Write-Host "Ready. Build with:"
    Write-Host "  dotnet build src/abbctl/abbctl.csproj -c Release"
    Write-Host "Then run: src/abbctl/bin/Release/abbctl.exe scan"
    Write-Host "(for a virtual controller, start the station in RobotStudio first)"
    exit 0
}
exit 1
