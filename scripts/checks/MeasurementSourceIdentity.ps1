function Get-MeasurementSourceIdentity([string] $RepositoryPath) {
    $commit = & git -C $RepositoryPath rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw '无法读取源码提交。' }
    $status = @(& git -C $RepositoryPath status --porcelain)
    if ($LASTEXITCODE -ne 0) { throw '无法读取工作树状态。' }
    $apiBlob = & git -C $RepositoryPath hash-object -- docs/contracts/public-api.txt
    if ($LASTEXITCODE -ne 0) { throw '无法读取当前 public-api Git blob。' }
    $paths = @(& git -C $RepositoryPath -c core.quotePath=false ls-files --cached --others --exclude-standard)
    if ($LASTEXITCODE -ne 0) { throw '无法列出源码文件。' }
    $files = @($paths | Sort-Object -CaseSensitive -Unique | ForEach-Object {
        $path = Join-Path $RepositoryPath $_
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            [ordered]@{ path = $_; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
        }
    })
    [ordered]@{
        commit = $commit
        dirty = $status.Count -ne 0
        git_status = $status
        public_api_git_blob = $apiBlob
        contract_baseline = '6032c70'
        build_configuration = 'ExportRelease'
        files = $files
    }
}
