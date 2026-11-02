import copy
import json
from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import summarize

MIB = 1048576


class SoakMemoryTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.runs = []

    def add(self, growth=100, delta=100, platform='Windows', seconds=7200):
        steady = {'samples': 1, 'private_bytes': 4 * 1024 * MIB,
                  'working_set_bytes': 3 * 1024 * MIB, 'video_bytes': 2 * 1024 * MIB}
        report = {'outcome': 'completed', 'assessment': {'items': [
            {'metric': key, 'status': 'failed'} for key in
            ('retired_heap_delta', 'private_growth', 'game_working_set')]}, 'identity': {
            'os': platform, 'scene_budget': {'initial_map': copy.deepcopy(steady),
            'soak': {'seconds': seconds, 'segments': 2, 'memory': {'reset_heaps': [
                {'phase': 'reset', 'segment': 1, 'live_bytes': 1000},
                {'phase': 'reset', 'segment': 2, 'live_bytes': 1000 + delta * MIB},
                {'phase': 'final', 'segment': 2, 'live_bytes': 9999999999}]}}}}}
        records = [{'kind': 'natural-map', 'at': at, 'memory': {
            **steady, 'private_bytes': value * MIB}} for at, value in (
                (100, 0), (1900, 1000), (3700, 0), (5500, growth / 2), (7300, growth))]
        records.append({'kind': 'open', 'steady': copy.deepcopy(steady)})
        self.runs.append((report, records, None))
        return report, records

    def result(self):
        summaries = []
        for index, (report, records, problem) in enumerate(self.runs, 1):
            directory = self.root / str(index)
            directory.mkdir(exist_ok=True)
            (directory / 'report.json').write_text(json.dumps(report))
            (directory / 'scene-operations.jsonl').write_text(''.join(json.dumps(r) + '\n' for r in records))
            (directory / 'process.json').write_text(problem or '{"exit_code":0,"timed_out":false,"cleanup_succeeded":true}')
            summaries.append({'scenario': 'scene-soak', 'report': f'{index}/report.json', 'succeeded': True})
        (self.root / 'summary.json').write_text(json.dumps({'runs': summaries}))
        return summarize.summarize_directories([self.root])['scenarios']['scene-soak']['memory_assessment']['A']

    def test_three_runs_reassess_old_status_and_inclusive_limits(self):
        for _ in range(3):
            self.add()
        result = self.result()
        for key in ('retired_heap_delta', 'private_growth_median', 'private_growth_maximum',
                    'game_private', 'game_working_set', 'game_render'):
            self.assertEqual('passed', result[key]['status'], key)
        self.assertEqual(100 * MIB, result['private_growth_median']['measured'])

    def test_second_hour_reuses_bracketed_ols_and_excludes_warmup(self):
        _, records = self.add()
        for record, (at, value) in zip(records, ((100, 0), (1901, 3), (3702, 3), (5503, 7), (7304, 11))):
            record['at'] = at
            record['memory']['private_bytes'] = value * MIB
        self.assertAlmostEqual(10080 / 1801 * MIB, self.result()['runs'][0]['private_growth']['measured'])

    def test_unknown_heap_segments_and_short_runs_cannot_pass(self):
        for change in ('missing', 'bad', 'duplicate', 'first', 'short', 'phase', 'segments', 'fraction'):
            with self.subTest(change=change):
                self.runs = []
                report, _ = self.add(seconds=7199 if change == 'short' else 7200)
                heaps = report['identity']['scene_budget']['soak']['memory']['reset_heaps']
                if change == 'missing': heaps.pop(1)
                if change == 'bad': heaps[1]['live_bytes'] = True
                if change == 'duplicate': heaps[1]['segment'] = 1
                if change == 'first': heaps[0]['segment'] = 0
                if change == 'phase': heaps[1].pop('phase')
                if change == 'segments': report['identity']['scene_budget']['soak'].pop('segments')
                if change == 'fraction': report['identity']['scene_budget']['soak']['segments'] = 2.5
                self.assertEqual('unknown', self.result()['runs'][0]['retired_heap_delta']['status'])

    def test_growth_needs_three_valid_runs_and_all_runs_are_used(self):
        for _ in range(2): self.add(growth=0)
        self.assertEqual('unknown', self.result()['private_growth_median']['status'])
        self.assertEqual('unknown', self.result()['retired_heap_delta']['status'])
        self.add(growth=200)
        self.add(growth=200)
        self.assertEqual('passed', self.result()['private_growth_median']['status'])
        self.add(growth=200)
        self.assertEqual('failed', self.result()['private_growth_median']['status'])

    def test_known_excess_survives_unknown_runs(self):
        self.add(growth=201, delta=101)
        self.add(seconds=1)
        result = self.result()
        self.assertEqual('unknown', result['private_growth_maximum']['status'])
        self.assertEqual('unknown', result['retired_heap_delta']['status'])
        self.assertEqual(1, result['retired_heap_delta']['unknown_runs'])

    def test_absolute_counters_platform_and_missing_values(self):
        for platform, working in (('Linux 6.8', 'recorded'), ('Windows 11', 'passed'), (None, 'unknown')):
            with self.subTest(platform=platform):
                self.runs = []
                report, records = self.add(platform=platform)
                self.assertEqual(working, self.result()['runs'][0]['game_working_set']['status'])
                for counter, key in (('private_bytes', 'game_private'), ('video_bytes', 'game_render')):
                    records[-1]['steady'][counter] += 1
                    self.assertEqual('failed', self.result()['runs'][0][key]['status'])
                    records[-1]['steady'][counter] = None
                    self.assertEqual('unknown', self.result()['runs'][0][key]['status'])

    def test_partial_hour_missing_counter_and_failed_process_never_pass(self):
        for condition in ('partial', 'counter', 'process', 'json'):
            with self.subTest(condition=condition):
                self.runs = []
                _, records = self.add()
                if condition == 'partial': records[-2]['at'] = 7200
                if condition == 'counter': records[-2]['memory']['private_bytes'] = None
                if condition in ('process', 'json'):
                    report, records, _ = self.runs[0]
                    self.runs[0] = report, records, '{}' if condition == 'process' else '{'
                self.assertEqual('unknown', self.result()['runs'][0]['private_growth']['status'])

    def test_each_limit_has_below_equal_and_above_values(self):
        for value, status in ((99, 'passed'), (100, 'passed'), (101, 'failed')):
            self.runs = []
            self.add(delta=value)
            self.assertEqual(status, self.result()['runs'][0]['retired_heap_delta']['status'])
        for value, status in ((199, 'passed'), (200, 'passed'), (201, 'failed')):
            self.runs = []
            self.add(growth=value)
            self.assertEqual(status, self.result()['runs'][0]['private_growth']['status'])
        for value, status in ((99, 'passed'), (100, 'passed'), (101, 'failed')):
            self.runs = []
            for _ in range(3): self.add(growth=value)
            self.assertEqual(status, self.result()['private_growth_median']['status'])

    def test_every_recorded_steady_source_contributes_and_raw_peaks_do_not(self):
        for field in ('initial_map', 'editor_steady', 'after_editor_map', 'final_map', 'editor_sequence'):
            self.runs = []
            report, records = self.add()
            sample = copy.deepcopy(records[-1]['steady'])
            sample['private_bytes'] += 1
            budget = report['identity']['scene_budget']
            budget[field] = {'after': sample} if field == 'editor_sequence' else sample
            self.assertEqual('failed', self.result()['runs'][0]['game_private']['status'])
        self.runs = []
        report, records = self.add()
        report['memory'] = {'private_peak_bytes': 999999999999}
        records[-1]['peak_watermark_before'] = {'private_bytes': 999999999999}
        self.assertEqual('passed', self.result()['runs'][0]['game_private']['status'])
        records[0]['memory']['samples'] = 0
        self.assertEqual('unknown', self.result()['runs'][0]['game_private']['status'])

    def test_unknown_run_blocks_otherwise_passing_combined_results(self):
        for _ in range(3): self.add(growth=0)
        report, records = self.add(growth=0)
        self.runs[-1] = report, records, '{}'
        result = self.result()
        for key in ('retired_heap_delta', 'private_growth_median', 'private_growth_maximum',
                    'game_private', 'game_working_set', 'game_render'):
            self.assertEqual('unknown', result[key]['status'], key)

    def test_windows_working_set_cap_and_linux_record_only_have_distinct_contracts(self):
        for platform, status in (('Windows', 'failed'), ('Linux', 'recorded')):
            self.runs = []
            _, records = self.add(platform=platform)
            records[-1]['steady']['working_set_bytes'] += 1
            item = self.result()['runs'][0]['game_working_set']
            self.assertEqual(status, item['status'])
            self.assertEqual(platform == 'Windows', 'threshold' in item)
            self.assertEqual(platform == 'Windows', 'excess' in item)

    def test_known_violations_with_three_valid_runs_remain_failed_despite_unknown_run(self):
        for _ in range(3): self.add(growth=201, delta=101)
        report, records = self.add()
        self.runs[-1] = report, records, '{}'
        result = self.result()
        for key, excess in (('retired_heap_delta', MIB), ('private_growth_median', 101 * MIB),
                            ('private_growth_maximum', MIB)):
            self.assertEqual('failed', result[key]['status'])
            self.assertEqual(1, result[key]['unknown_runs'])
            self.assertEqual(excess, result[key]['excess'])

    def test_truncated_operations_never_publish_valid_prefix(self):
        for _ in range(3): self.add()
        self.result()
        path = self.root / '1' / 'scene-operations.jsonl'
        with path.open('a') as stream: stream.write('{')
        result = summarize.summarize_directories([self.root])['scenarios']['scene-soak']['memory_assessment']['A']
        self.assertEqual('unknown', result['game_private']['status'])
        self.assertEqual('operations_unreadable_jsonl', result['runs'][0]['game_private']['reason'])

    def test_missing_or_malformed_reset_array_is_unknown_through_summary(self):
        for rows in (None, {}, 'bad', 1):
            self.runs = []
            report, _ = self.add()
            memory = report['identity']['scene_budget']['soak']['memory']
            if rows is None: memory.pop('reset_heaps')
            else: memory['reset_heaps'] = rows
            self.assertEqual('unknown', self.result()['runs'][0]['retired_heap_delta']['status'])

    def test_short_declared_soak_cannot_validate_complete_raw_hour_or_excess(self):
        self.add(seconds=7199, growth=201, delta=101)
        run = self.result()['runs'][0]
        self.assertEqual('unknown', run['private_growth']['status'])
        self.assertIsNone(run['private_growth']['excess'])
        self.assertIsNone(run['retired_heap_delta']['excess'])

    def test_linux_and_unknown_or_mixed_platforms_never_use_windows_limit(self):
        for condition in ('process', 'empty_report', 'missing_identity', 'windows'):
            with self.subTest(condition=condition):
                self.runs = []
                for _ in range(4):
                    _, records = self.add(platform='Linux')
                    records[-1]['steady']['working_set_bytes'] += 1
                report, records = self.runs[-1][:2]
                if condition == 'process': self.runs[-1] = report, records, '{}'
                if condition == 'empty_report': self.runs[-1] = {}, records, None
                if condition == 'missing_identity': report.pop('identity')
                if condition == 'windows': report['identity']['os'] = 'Windows'
                item = self.result()['game_working_set']
                self.assertEqual('unknown', item['status'])
                self.assertEqual(3072 * MIB + 1, item['measured'])
                self.assertTrue(item['reason'])
                self.assertNotIn('threshold', item)
                self.assertNotIn('excess', item)

    def test_raw_failure_overrides_old_passing_item_statuses(self):
        for _ in range(3):
            report, records = self.add(growth=201, delta=101)
            records[-1]['steady']['working_set_bytes'] += 1
            for item in report['assessment']['items']: item['status'] = 'passed'
        result = self.result()
        for key in ('retired_heap_delta', 'private_growth_maximum', 'game_working_set'):
            self.assertEqual('failed', result[key]['status'])

    def test_linux_complete_aggregate_records_only_after_three_valid_runs(self):
        for _ in range(2): self.add(platform='Linux')
        item = self.result()['game_working_set']
        self.assertEqual('unknown', item['status'])
        self.assertNotIn('threshold', item)
        self.add(platform='Linux')
        item = self.result()['game_working_set']
        self.assertEqual('recorded', item['status'])
        self.assertNotIn('threshold', item)
        self.assertNotIn('excess', item)


if __name__ == '__main__':
    unittest.main()
