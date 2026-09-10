# Install (or update) the servers as .NET global tools on Windows.
# Usage: pwsh scripts/install-tools.ps1 [-Servers mcp-roslyn,mcp-index]
param(
    [string[]] $Servers = @('mcp-filesystem', 'mcp-database', 'mcp-roslyn', 'mcp-index', 'mcp-learnings'),
    [string] $Source = 'nupkg'
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
Set-Location $root

$projects = @{
    'mcp-filesystem' = 'src/servers/McpServices.FileSystem/McpServices.FileSystem.csproj'
    'mcp-database'   = 'src/servers/McpServices.Database/McpServices.Database.csproj'
    'mcp-roslyn'     = 'src/servers/McpServices.Roslyn/McpServices.Roslyn.csproj'
    'mcp-index'      = 'src/servers/McpServices.Index/McpServices.Index.csproj'
    'mcp-learnings'  = 'src/servers/McpServices.Learnings/McpServices.Learnings.csproj'
}

if (-not (Test-Path $Source) -or -not (Get-ChildItem $Source -ErrorAction SilentlyContinue)) {
    foreach ($project in $projects.Values) { dotnet pack $project --configuration Release --output $Source }
}

$version = ([xml](Get-Content Directory.Build.props)).Project.PropertyGroup.Version
$installed = dotnet tool list --global
foreach ($server in $Servers) {
    $project = $projects[$server]
    if (-not $project) { throw "Unknown server '$server'" }
    $package = ([xml](Get-Content $project)).Project.PropertyGroup.PackageId
    Write-Host "==> $server ($package $version)"
    if ($installed -match "(?i)^$package\s") {
        dotnet tool update --global $package --add-source $Source --version $version
    } else {
        dotnet tool install --global $package --add-source $Source --version $version
    }
}
Write-Host "Installed. Try: mcp-filesystem --help"
