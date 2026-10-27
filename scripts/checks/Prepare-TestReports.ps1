param([string] $Directory, [string] $Name)

$ErrorActionPreference = 'Stop'
$counts = $null
foreach ($file in Get-ChildItem -LiteralPath $Directory -Filter '*.trx' -File -Recurse) {
    $report = [xml]::new()
    $report.PreserveWhitespace = $true
    $report.Load($file.FullName)
    # MTP 写入本机身份；只规范化框架属性，诊断正文仍由发布检查验证。
    foreach ($attribute in $report.SelectNodes('/*/@name | /*/@runUser | //@computerName | //@runDeploymentRoot')) {
        $attribute.Value = '<redacted>'
    }
    foreach ($attribute in $report.SelectNodes('//@codeBase | //@storage')) {
        $attribute.Value = [IO.Path]::GetFileName($attribute.Value)
    }
    $report.Save($file.FullName)
    if ($file.FullName -eq (Join-Path $Directory "$Name.trx")) {
        $counters = $report.TestRun.ResultSummary.Counters
        $counts = @{}
        foreach ($count in @('total', 'executed', 'passed', 'failed')) { $counts[$count] = [int] $counters.$count }
        $counts.skipped = [int] $counters.notExecuted + [int] $counters.inconclusive
    }
}
$counts
