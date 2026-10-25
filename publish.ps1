# Builds both apps into .\publish (following.exe + following-background.exe side by side).
# Requires the .NET 10 runtime on the machine; add --self-contained -r win-x64 to bundle it instead.
$ErrorActionPreference = 'Stop'
$out = Join-Path $PSScriptRoot 'publish'
foreach ($project in 'Following', 'Following.Background') {
    dotnet publish "$PSScriptRoot/src/$project/$project.csproj" -c Release -o $out
    if ($LASTEXITCODE -ne 0) { throw "Publishing $project failed." }
}
Write-Host "`nPublished to $out"
