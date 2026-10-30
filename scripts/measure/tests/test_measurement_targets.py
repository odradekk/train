"""Execute parameter binding and production command construction without launching games."""
import json
import subprocess
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]


def run(script):
    result = subprocess.run(['pwsh', '-NoProfile', '-Command', script], cwd=ROOT,
                            capture_output=True, text=True, encoding='utf-8')
    if result.returncode:
        raise AssertionError(result.stdout + result.stderr)
    return json.loads(result.stdout)


class MeasurementTargetTests(unittest.TestCase):
    def test_binding_defaults_and_invalid_values(self):
        for filename in ('measure.ps1', 'run-measurement.ps1'):
            script = r'''
$tokens=$null; $errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile('scripts/measure/FILENAME',[ref]$tokens,[ref]$errors)
$probe=[scriptblock]::Create($ast.ParamBlock.Extent.Text + "`n" + '$SoakProfile' )
$required=if ('FILENAME' -eq 'measure.ps1') { @{ExecutablePath='x';InputPath='x';OutputDirectory='x';RepositoryPath='.'} } else { @{CacheDirectory='x';Scenario='scene-soak';Root='.'} }
$default=& $probe @required
$valid=@(foreach($profile in 'standard-world','crowded-station','busy-combat','long-consist') {
    & $probe @required -SoakProfile $profile -TrainId 1 -NodeId 9223372036854775807 -EdgeId 23
})
$invalid=@(foreach($key in 'TrainId','NodeId','EdgeId','SoakProfile') {
    foreach($value in $(if($key -eq 'SoakProfile') {@('invalid')} else {@('0','-1','9223372036854775808')})) {
        $values=$required.Clone();$values[$key]=$value
        try { & $probe @values | Out-Null; $false } catch { $true }
    }
})
@{default=$default;valid=$valid;invalid=$invalid}|ConvertTo-Json -Compress
'''.replace('FILENAME', filename).replace("+ '\\n$SoakProfile'", '+ "`n$SoakProfile"')
            data = run(script)
            self.assertEqual('standard-world', data['default'])
            self.assertEqual(['standard-world', 'crowded-station', 'busy-combat', 'long-consist'], data['valid'])
            self.assertEqual([True] * 10, data['invalid'])

    def test_runner_reference_and_scenarios_share_targets(self):
        data = run(r'''
$source=Get-Content -Raw scripts/measure/run-measurement.ps1
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseInput($source,[ref]$tokens,[ref]$errors)
$commands=$ast.FindAll({param($n) $n -is [Management.Automation.Language.CommandAst] -and $n.Extent.Text.StartsWith('& (Join-Path') -and $n.Extent.Text.Contains("'measure.ps1'")},$true)
function Invoke-Probe {param($RepositoryPath,$ExecutablePath,$InputPath,$OutputDirectory,$Scenario,$Repeat,$Machine,$DurationSeconds,$ReferencePath,[switch]$NodeReadyHold,$TrainId,$NodeId,$EdgeId,$SoakProfile)
    @{scenario=@($Scenario);train=$TrainId;node=$NodeId;edge=$EdgeId;profile=$SoakProfile}
}
$Root='.';$executable='x';$measurementInput=@{path='x';directory='.'};$Scenario='scene-node,scene-edge';$Repeat=3;$DurationSeconds=0;$NodeReadyHold=$false
$SoakProfile='long-consist';$TrainId=21L;$NodeId=15L;$EdgeId=23L
$PSBoundParameters.Clear();foreach($key in 'TrainId','NodeId','EdgeId') {$PSBoundParameters[$key]=Get-Variable $key -ValueOnly}
# Execute the shared splat construction, then each production call against a recorder.
$start=$source.IndexOf('$targetParameters =')
$end=$source.IndexOf('$release =', $start)
Invoke-Expression $source.Substring($start,$end-$start)
$result=foreach($command in $commands) {
    $text='Invoke-Probe'+$command.Extent.Text.Substring($command.Extent.Text.IndexOf(')')+1)
    $text=($text -replace '(?s)\s+\*>.*$','').TrimEnd().TrimEnd([char]96)
    Invoke-Expression $text
}
@($result)|ConvertTo-Json -Depth 5 -Compress
''')
        self.assertEqual(2, len(data))
        for record in data:
            self.assertEqual((21, 15, 23, 'long-consist'),
                             (record['train'], record['node'], record['edge'], record['profile']))
        self.assertEqual(['scene-reference'], data[0]['scenario'])

    def test_game_arguments(self):
        data = run(r'''
$source=Get-Content -Raw scripts/measure/measure.ps1
$start=$source.IndexOf('$arguments = @(');$end=$source.IndexOf('$launch = @($ExecutablePath)', $start)
$construct=[scriptblock]::Create($source.Substring($start,$end-$start))
$name='scene-soak';$runDirectory='.';$InputPath='input';$reportPath='report';$identityPath='identity';$DurationSeconds=0
$StreamingDiagnostics=$false;$lowOrbit=$false;$NodeReadyHold=$false;$ReferencePath=$null
$TrainId=21L;$NodeId=15L;$EdgeId=23L;$SoakProfile='long-consist'
$records=foreach($bound in @(@{},@{TrainId=21L;NodeId=15L;EdgeId=23L})) {
    $PSBoundParameters.Clear();foreach($key in $bound.Keys) {$PSBoundParameters[$key]=$bound[$key]};Invoke-Expression $construct.ToString()
    ,@($arguments)
}
ConvertTo-Json -InputObject @($records) -Depth 5 -Compress
''')
        for args in data:
            self.assertEqual('long-consist', args[args.index('--measure-soak-profile') + 1])
        for key in ('train', 'node', 'edge'):
            self.assertNotIn('--measure-' + key, data[0])
        for key, value in (('train', '21'), ('node', '15'), ('edge', '23')):
            self.assertEqual(value, data[1][data[1].index('--measure-' + key) + 1])

    def test_workflow_uses_selected_input_and_targets_for_soak(self):
        source=(ROOT / '.github/workflows/windows-checks.yml').read_text(encoding='utf-8')
        start=source.index('          $parameters = @{ Scenario = $env:MEASURE_SCENARIOS;')
        end=source.index('          & "$env:RUNNER_TEMP/measure-tools/scripts/measure/run-measurement.ps1"', start)
        script=r'''
$env:CI_SELECTION='window-soak';$env:MEASURE_SCENARIOS='scene-node';$env:MEASURE_INPUT_SET='scene-scale'
$env:MEASURE_INPUT='examples/long-consist/save.json';$env:MEASURE_REPEAT='3';$env:MEASURE_SECONDS='0';$env:SOAK_SECONDS='9000'
$env:MEASURE_TRAIN='21';$env:MEASURE_NODE='15';$env:MEASURE_EDGE='23';$env:MEASURE_SOAK_PROFILE='long-consist'
''' + source[start:end] + '\n$parameters | ConvertTo-Json -Compress'
        data=run(script)
        self.assertEqual('examples/long-consist/save.json', data['InputPath'])
        self.assertEqual('example', data['InputSet'])
        self.assertEqual(('21','15','23','long-consist'), tuple(data[k] for k in ('TrainId','NodeId','EdgeId','SoakProfile')))
        self.assertEqual(('scene-soak',1,'9000'), (data['Scenario'],data['Repeat'],data['DurationSeconds']))


if __name__ == '__main__':
    unittest.main()
