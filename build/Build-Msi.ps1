[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PublishDir,

    [Parameter(Mandatory = $true)]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$OutputPath,

    [string]$ProductName = "RdpSwitcher",

    [string]$Manufacturer = "RdpSwitcher"
)

$ErrorActionPreference = "Stop"

function ConvertTo-WixId {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Prefix,

        [Parameter(Mandatory = $true)]
        [string]$Value
    )

    $id = $Value -replace '[^A-Za-z0-9_]', '_'
    if ($id -notmatch '^[A-Za-z_]') {
        $id = "_$id"
    }

    return "$Prefix$id"
}

function New-StableGuid {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Value
    )

    $bytes = [System.Text.Encoding]::UTF8.GetBytes($Value)
    $hash = [System.Security.Cryptography.MD5]::HashData($bytes)
    $hash[6] = ($hash[6] -band 0x0F) -bor 0x30
    $hash[8] = ($hash[8] -band 0x3F) -bor 0x80
    return ([guid]::new($hash)).ToString("B").ToUpperInvariant()
}

function Escape-Xml {
    param(
        [AllowEmptyString()]
        [string]$Value
    )

    return [System.Security.SecurityElement]::Escape($Value)
}

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "MSI version must be three numeric parts, for example 1.2.3. Value: $Version"
}

$publishPath = (Resolve-Path -LiteralPath $PublishDir).Path
$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$iconPath = Join-Path $repoRoot "Assets\RdpSwitcher.ico"
$licensePath = Join-Path $repoRoot "LICENSE"
$outputFullPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
$outputDirectory = Split-Path -Parent $outputFullPath

if (-not (Test-Path -LiteralPath $iconPath)) {
    throw "Icon file was not found: $iconPath"
}

New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null

$nestedDirectories = Get-ChildItem -LiteralPath $publishPath -Directory
if ($nestedDirectories.Count -gt 0) {
    throw "PublishDir must contain a flat publish output. Subdirectories were found in: $publishPath"
}

$files = @(Get-ChildItem -LiteralPath $publishPath -File | Sort-Object Name)
$mainExe = $files | Where-Object { $_.Name -ieq "RdpSwitcher.exe" } | Select-Object -First 1
if ($null -eq $mainExe) {
    throw "RdpSwitcher.exe was not found in publish output: $publishPath"
}

$componentXml = [System.Text.StringBuilder]::new()
$componentRefXml = [System.Text.StringBuilder]::new()

foreach ($file in $files) {
    $componentId = ConvertTo-WixId -Prefix "cmp_" -Value $file.Name
    $fileId = ConvertTo-WixId -Prefix "fil_" -Value $file.Name
    $source = Escape-Xml -Value $file.FullName
    $componentGuid = New-StableGuid -Value "RdpSwitcher|$($file.Name)"

    [void]$componentXml.AppendLine("        <Component Id=""$componentId"" Guid=""$componentGuid"">")
    [void]$componentXml.AppendLine("          <File Id=""$fileId"" Source=""$source"" KeyPath=""yes"" />")

    if ($file.Name -ieq "RdpSwitcher.exe") {
        [void]$componentXml.AppendLine("          <Shortcut Id=""StartMenuShortcut"" Directory=""ApplicationProgramsFolder"" Name=""RdpSwitcher"" Description=""RdpSwitcher"" Target=""[INSTALLFOLDER]RdpSwitcher.exe"" WorkingDirectory=""INSTALLFOLDER"" Icon=""RdpSwitcherIcon"" />")
        [void]$componentXml.AppendLine("          <RemoveFolder Id=""RemoveApplicationProgramsFolder"" Directory=""ApplicationProgramsFolder"" On=""uninstall"" />")
    }

    [void]$componentXml.AppendLine("        </Component>")
    [void]$componentRefXml.AppendLine("      <ComponentRef Id=""$componentId"" />")
}

if (Test-Path -LiteralPath $licensePath) {
    $componentId = "cmp_LICENSE"
    $source = Escape-Xml -Value $licensePath
    $componentGuid = New-StableGuid -Value "RdpSwitcher|LICENSE"
    [void]$componentXml.AppendLine("        <Component Id=""$componentId"" Guid=""$componentGuid"">")
    [void]$componentXml.AppendLine("          <File Id=""fil_LICENSE"" Source=""$source"" Name=""LICENSE.txt"" KeyPath=""yes"" />")
    [void]$componentXml.AppendLine("        </Component>")
    [void]$componentRefXml.AppendLine("      <ComponentRef Id=""$componentId"" />")
}

$escapedProductName = Escape-Xml -Value $ProductName
$escapedManufacturer = Escape-Xml -Value $Manufacturer
$escapedIconPath = Escape-Xml -Value $iconPath
$upgradeCode = "{7E0B36D1-64ED-44ED-829D-1865588A7643}"
$tempDirectory = Join-Path ([System.IO.Path]::GetTempPath()) "RdpSwitcherMsi-$([guid]::NewGuid())"
$wxsPath = Join-Path $tempDirectory "RdpSwitcher.wxs"

New-Item -ItemType Directory -Force -Path $tempDirectory | Out-Null

$wxs = @"
<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">
  <Package Id="RdpSwitcher" Name="$escapedProductName" Manufacturer="$escapedManufacturer" Version="$Version" UpgradeCode="$upgradeCode" Scope="perMachine" InstallerVersion="500" Compressed="yes">
    <MajorUpgrade DowngradeErrorMessage="A newer version of [ProductName] is already installed." />
    <MediaTemplate EmbedCab="yes" />
    <Icon Id="RdpSwitcherIcon" SourceFile="$escapedIconPath" />
    <Property Id="ARPPRODUCTICON" Value="RdpSwitcherIcon" />

    <StandardDirectory Id="ProgramFiles64Folder">
      <Directory Id="INSTALLFOLDER" Name="RdpSwitcher">
$componentXml      </Directory>
    </StandardDirectory>

    <StandardDirectory Id="ProgramMenuFolder">
      <Directory Id="ApplicationProgramsFolder" Name="RdpSwitcher" />
    </StandardDirectory>

    <Feature Id="DefaultFeature" Title="RdpSwitcher" Level="1">
$componentRefXml    </Feature>
  </Package>
</Wix>
"@

Set-Content -LiteralPath $wxsPath -Value $wxs -Encoding UTF8

try {
    wix build $wxsPath -arch x64 -out $outputFullPath
}
finally {
    Remove-Item -LiteralPath $tempDirectory -Recurse -Force
}
