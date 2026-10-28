"""Script argument construction only; these tests never launch the game."""
import json
import subprocess
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]


class NodeReadyHoldTests(unittest.TestCase):
    def test_workflow_enables_hold_only_for_measure_with_true_input(self):
        workflow = (ROOT / '.github/workflows/windows-checks.yml').read_text(encoding='utf-8')
        declaration = workflow.split('      measure_node_ready_hold:', 1)[1].split('      measure_seconds:', 1)[0]
        self.assertIn('type: boolean', declaration)
        self.assertIn('default: false', declaration)
        self.assertIn('MEASURE_NODE_READY_HOLD: ${{ inputs.measure_node_ready_hold }}', workflow)
        start = workflow.index('          $parameters = @{ Scenario = $env:MEASURE_SCENARIOS;')
        end = workflow.index('          & "$env:RUNNER_TEMP/measure-tools/scripts/measure/run-measurement.ps1"', start)
        construct = workflow[start:end]
        script = r'''
param($selection, $hold)
$env:CI_SELECTION = $selection; $env:MEASURE_NODE_READY_HOLD = $hold
$env:MEASURE_SCENARIOS = 'scene-node-first'; $env:MEASURE_INPUT_SET = 'example'
$env:MEASURE_INPUT = 'examples/standard-world/save.json'; $env:MEASURE_REPEAT = '3'
$env:MEASURE_SECONDS = '0'; $env:SOAK_SECONDS = '9000'
''' + construct + '\n$parameters | ConvertTo-Json -Compress\n'
        for selection, value, enabled in (('measure', 'true', True), ('measure', 'false', False),
                                           ('measure', '', False), ('window-soak', 'true', False)):
            with self.subTest(selection=selection, value=value):
                result = subprocess.run(['pwsh', '-NoProfile', '-Command', '& {\n' + script + '\n}',
                                         selection, value], capture_output=True, text=True, encoding='utf-8')
                self.assertEqual(0, result.returncode, result.stdout + result.stderr)
                self.assertEqual(enabled, json.loads(result.stdout).get('NodeReadyHold', False))

    def arguments(self, enabled):
        script = r'''
param($root, $enabled)
$NodeReadyHold = $enabled -eq 'true'
$Scenario = @('scene-route-1x', 'scene-route-8x', 'scene-node-first', 'scene-reference')
$source = Get-Content -Raw (Join-Path $root 'scripts/measure/measure.ps1')
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseInput($source, [ref]$tokens, [ref]$errors)
# Execute the production hold validation, if present, and argument construction only.
$checks = $ast.FindAll({ param($node)
    $node -is [Management.Automation.Language.IfStatementAst] -and
    $node.Extent.Text -match "throw '-NodeReadyHold"
}, $true)
foreach ($check in $checks) { & ([scriptblock]::Create($check.Extent.Text)) }
$start = $source.IndexOf('$arguments = @(')
$end = $source.IndexOf('$launch = @($ExecutablePath)', $start)
$construct = [scriptblock]::Create($source.Substring($start, $end - $start))
$runDirectory = '.'; $InputPath = 'input'; $reportPath = 'report'; $identityPath = 'identity'
$DurationSeconds = 0; $StreamingDiagnostics = $false; $lowOrbit = $false; $ReferencePath = $null
$result = @{}
foreach ($name in $Scenario) {
    . $construct
    $result[$name] = @($arguments)
}
$result | ConvertTo-Json -Depth 5 -Compress
'''
        result = subprocess.run(['pwsh', '-NoProfile', '-Command', '& {\n' + script + '\n}',
                                 str(ROOT), str(enabled).lower()], capture_output=True, text=True, encoding="utf-8")
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        return json.loads(result.stdout)

    def test_runner_forwards_switch_to_reference_and_scenarios(self):
        script = r"""
param($root)
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $root 'scripts/measure/run-measurement.ps1'), [ref]$tokens, [ref]$errors)
$commands = $ast.FindAll({ param($node)
    $node -is [Management.Automation.Language.CommandAst] -and
    $node.Extent.Text.StartsWith("& (Join-Path") -and $node.Extent.Text.Contains("'measure.ps1'")
}, $true)
function Invoke-Probe { param($RepositoryPath, $ExecutablePath, $InputPath, $OutputDirectory,
    $Scenario, $Repeat, $Machine, $DurationSeconds, $ReferencePath, [switch]$NodeReadyHold)
    [pscustomobject]@{scenario = @($Scenario); hold = [bool]$NodeReadyHold}
}
$Root = '.'; $executable = 'program'; $measurementInput = @{path='input';directory='.'}
$Scenario = 'scene-route-1x,scene-route-8x,scene-node-first'; $Repeat = 3; $DurationSeconds = 0
$result = foreach ($NodeReadyHold in @($false, $true)) {
    foreach ($command in $commands) {
        $text = 'Invoke-Probe' + $command.Extent.Text.Substring($command.Extent.Text.IndexOf(')') + 1)
        $text = ($text -replace '(?s)\s+\*>.*$', '').TrimEnd().TrimEnd([char]96)
        & ([scriptblock]::Create($text))
    }
}
@($result) | ConvertTo-Json -Depth 5 -Compress
"""
        result = subprocess.run(['pwsh', '-NoProfile', '-Command', '& {\n' + script + '\n}', str(ROOT)],
                                capture_output=True, text=True, encoding='utf-8')
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        records = json.loads(result.stdout)
        self.assertEqual([False, False, True, True], [record['hold'] for record in records])
        self.assertEqual(['scene-reference'], records[2]['scenario'])
        self.assertEqual(['scene-route-1x', 'scene-route-8x', 'scene-node-first'], records[3]['scenario'])

    def test_mixed_scenarios_hold_only_node_and_reference(self):
        result = self.arguments(True)
        for scenario, arguments in result.items():
            self.assertEqual(scenario in ('scene-node-first', 'scene-reference'),
                             '--measure-node-ready-hold' in arguments, scenario)

    def test_default_does_not_add_hold_to_any_scenario(self):
        for arguments in self.arguments(False).values():
            self.assertNotIn('--measure-node-ready-hold', arguments)


if __name__ == '__main__':
    unittest.main()
