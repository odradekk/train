function Get-TestSelection {
    param([string] $BaseRef, [switch] $All, [string[]] $Project, [object[]] $Projects, [string] $OutputDirectory)

    $selection = [ordered]@{
        mode = if ($All) { 'all' } elseif ($Project.Count) { 'explicit' } else { 'affected' }
        baseRef = $BaseRef; mergeBase = $null; changes = @(); affectedProjects = @()
        projects = @(); contracts = [bool] $All; isolation = [bool] $All; schema = [bool] $All; analysis = $null
    }
    if ($All -or $Project.Count) {
        $selection.projects = @(if ($All) { $Projects.name } else { $Project })
        return $selection
    }

    # dotnet-affected requires Git's path casing and native separators, including on Windows.
    $root = [IO.Path]::GetFullPath((git rev-parse --show-toplevel).Trim())
    if ($LASTEXITCODE) { throw 'Cannot identify the Git worktree.' }
    $base = @(git merge-base $BaseRef HEAD 2>&1)
    if ($LASTEXITCODE) { throw "Cannot resolve comparison base '$BaseRef': $base" }
    $selection.mergeBase = ($base -join '').Trim()
    $tracked = @(git -c core.quotepath=false diff --name-only --no-renames $selection.mergeBase --)
    if ($LASTEXITCODE) { throw 'Cannot list changed tracked files.' }
    $untracked = @(git -c core.quotepath=false ls-files --others --exclude-standard)
    if ($LASTEXITCODE) { throw 'Cannot list untracked files.' }
    $files = @(@($tracked) + @($untracked) | Sort-Object -Unique)
    $sharedPatterns = @(
        'Contracts/*', 'Directory.Build.*', '*/Directory.Build.*', 'global.json',
        'docs/content/schemas/*', 'Tests/ContractSupport/*', 'Train.sln',
        '.config/dotnet-tools.json', 'scripts/test.ps1', 'scripts/checks/*'
    )
    $contractPatterns = @(
        'Contracts/*', '*.csproj', 'Directory.Build.*', '*/Directory.Build.*', 'Train.sln',
        'prototypes/*', 'Tests/ContractChecks/*', 'docs/contracts/public-api.txt', 'scripts/checks/project-boundaries.json',
        'builtin/*.png', 'builtin/*.glb', 'builtin/*.png.import', 'builtin/*.glb.import'
    )
    $isolationPatterns = @('*.csproj', 'Directory.Build.*', '*/Directory.Build.*', 'Train.sln', 'scripts/checks/project-boundaries.json', 'Tests/ContractChecks/*')
    foreach ($file in $files) {
        $selection.changes += @{ path = $file; project = $null; sharedRules = @($sharedPatterns | Where-Object { $file -like $_ }) }
        if (@($contractPatterns | Where-Object { $file -like $_ }).Count) { $selection.contracts = $true }
        if (@($isolationPatterns | Where-Object { $file -like $_ }).Count) { $selection.isolation = $true }
        if ($file -like 'docs/content/*') { $selection.schema = $true }
    }

    $arguments = @('affected', '-p', $root, '--filter-file-path', (Join-Path $root 'Train.sln'),
        '--from', $selection.mergeBase, '--uncommitted', 'all', '--format', 'json', '--output-dir', $OutputDirectory)
    $result = Invoke-HiddenProcess dotnet $arguments 120
    $result.Output | Set-Content (Join-Path $OutputDirectory 'selection.log')
    $selection.analysis = @{ command = @('dotnet') + $arguments; exitCode = $result.ExitCode; seconds = $result.DurationMs / 1000; timedOut = $result.TimedOut; cleanupSucceeded = $result.CleanupSucceeded }
    $selection.analysis | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $OutputDirectory 'selection-process.json')
    if ($result.ExitCode -notin @(0, 166) -or $result.TimedOut -or -not $result.CleanupSucceeded) {
        throw "dotnet-affected failed (exit=$($result.ExitCode), timeout=$($result.TimedOut)): $($result.Output)"
    }
    if ($result.ExitCode -eq 0) {
        $affected = Get-Content -LiteralPath (Join-Path $OutputDirectory 'affected.json') -Raw | ConvertFrom-Json
        $selection.affectedProjects = @($affected | ForEach-Object { [IO.Path]::GetRelativePath($root, $_.FilePath).Replace('\', '/') })
    }
    $selection.projects = @(if (@($selection.changes | Where-Object { $_.sharedRules.Count }).Count) {
        @($Projects.name)
    } else { @($Projects | Where-Object { $_.path -in $selection.affectedProjects } | ForEach-Object name) })

    $projectPaths = @(git -c core.quotepath=false ls-files --cached --others --exclude-standard -- '*.csproj')
    if ($LASTEXITCODE) { throw 'Cannot list project files for selection explanations.' }
    $owners = @($projectPaths | ForEach-Object {
        @{ path = $_; directory = [IO.Path]::GetDirectoryName($_).Replace('\', '/') }
    } | Sort-Object { $_.directory.Length } -Descending)
    foreach ($change in $selection.changes) {
        $owner = $owners | Where-Object { -not $_.directory -or $change.path.StartsWith($_.directory + '/', [StringComparison]::Ordinal) } | Select-Object -First 1
        if ($owner.path -in $selection.affectedProjects) { $change.project = $owner.path }
    }
    return $selection
}
