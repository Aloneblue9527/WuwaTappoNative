$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$path = Join-Path $root "docs/compatibility.json"
$json = Get-Content -LiteralPath $path -Raw -Encoding utf8 | ConvertFrom-Json

function Get-OfficialVersion([string]$hostUrl, [string]$hostFile) {
    $headers = @{ "User-Agent" = "WuwatappoNative compatibility sync" }
    $hostInfo = Invoke-RestMethod -Uri "$hostUrl/app/json-host/$hostFile" -Headers $headers
    if ([string]::IsNullOrWhiteSpace($hostInfo.url)) {
        throw "missing url in $hostUrl/app/json-host/$hostFile"
    }
    $manifest = Invoke-RestMethod -Uri $hostInfo.url -Headers $headers
    if ([string]::IsNullOrWhiteSpace($manifest.version)) {
        throw "missing version in $($hostInfo.url)"
    }
    return $manifest
}

function Get-VersionKey([string]$value) {
    $match = [regex]::Match(($value ?? "").Trim(), '^[vV]?(\d+)\.(\d+)\.(\d+)')
    if (-not $match.Success) { return $null }
    return [version]("{0}.{1}.{2}" -f $match.Groups[1].Value, $match.Groups[2].Value, $match.Groups[3].Value)
}

function Test-VersionGreater([string]$left, [string]$right) {
    $a = Get-VersionKey $left
    $b = Get-VersionKey $right
    if ($null -eq $a) { return $false }
    if ($null -eq $b) { return $true }
    return $a -gt $b
}

$cn = Get-OfficialVersion "https://mc-wuwatappo.kurogames-global.com" "latest_host.json"
$global = Get-OfficialVersion "https://wutheringwaves-wuwatappo.kurogames-global.com" "latest-global_host.json"

$changed = $false
if ($json.official.cn.latest -ne $cn.version) {
    $json.official.cn.latest = $cn.version
    $changed = $true
}
# minOta 允许维护者用 minOtaLocked 固定（例如官方已强制 1.0.5，但清单仍写 1.0.0）；
# 未锁定时也只允许向上提升，不能被较低的服务端值拉回去。
$cnFloor = if ($cn.force_manual_download) { $cn.version } else { $cn.min_ota_client_version }
if ($json.official.cn.minOtaLocked -ne $true -and
    -not [string]::IsNullOrWhiteSpace($cnFloor) -and
    (Test-VersionGreater $cnFloor $json.official.cn.minOta)) {
    $json.official.cn.minOta = $cnFloor
    $changed = $true
}
if ($json.official.global.latest -ne $global.version) {
    $json.official.global.latest = $global.version
    $changed = $true
}
$globalFloor = if ($global.force_manual_download) { $global.version } else { $global.min_ota_client_version }
if ($json.official.global.minOtaLocked -ne $true -and
    -not [string]::IsNullOrWhiteSpace($globalFloor) -and
    (Test-VersionGreater $globalFloor $json.official.global.minOta)) {
    $json.official.global.minOta = $globalFloor
    $changed = $true
}
if ($json.official.global.inheritCn -ne $false) {
    $json.official.global.inheritCn = $false
    $changed = $true
}

if (-not $changed) {
    Write-Host "Official versions unchanged."
    exit 0
}

$json.updatedAt = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
$tested = if ([string]::IsNullOrWhiteSpace($json.protocol.testedOfficial)) {
    "未验证"
} else {
    $json.protocol.testedOfficial
}
if ($json.messageLocked -ne $true) {
    $forced = [bool]$cn.force_manual_download -or [bool]$global.force_manual_download
    if ($forced) {
        $json.message = "官方客户端已强制更新至 $($cn.version)；当前协议验证基于 $tested，请使用已验证版本。"
    } else {
        $json.message = "官方客户端已更新至 $($cn.version)；当前协议验证基于 $tested，暂未发现破坏性变更。"
    }
} else {
    Write-Host "messageLocked=true，保留手动维护的兼容性说明。"
}
$json | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $path -Encoding utf8
Write-Host "Updated compatibility.json: cn=$($cn.version), global=$($global.version)"
