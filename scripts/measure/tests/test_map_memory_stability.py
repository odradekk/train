import json
from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import summarize


class MapMemoryStabilityTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.run = self.root / 'scene-map-1'
        self.run.mkdir()
        (self.root / 'summary.json').write_text(json.dumps({'runs': [{
            'scenario': 'scene-map', 'report': 'scene-map-1/report.json', 'succeeded': True}]}), encoding='utf-8')
        (self.run / 'report.json').write_text('{"outcome":"completed"}', encoding='utf-8')
        (self.run / 'process.json').write_text(
            '{"exit_code":0,"timed_out":false,"cleanup_succeeded":true}', encoding='utf-8')
        (self.run / 'scene-operations.jsonl').write_text('', encoding='utf-8')

    def observe(self, samples):
        (self.run / 'scene-frames.jsonl').write_text(''.join(json.dumps({
            'at': at, 'phase': phase, 'private_bytes': value, 'working_set_bytes': 200,
            'buffer_bytes': 20, 'texture_bytes': 50, 'video_bytes': 70}) + '\n'
            for at, phase, value in samples), encoding='utf-8')
        return self.result()

    def result(self):
        return summarize.summarize_directories([self.root])['scenarios']['scene-map']['memory_stability']['A'][0]

    def test_stable_map_uses_only_the_final_phase_tail_without_an_open_operation(self):
        result = self.observe([(0, 'load', 9000), (1, 'final-map', 8000),
                               (50, 'final-map', 100), (55, 'final-map', 101),
                               (60, 'final-map', 100), (61, 'other', 7000)])
        self.assertEqual(('stable', None, 50, 60, 3, .01), tuple(result[k] for k in (
            'status', 'reason', 'window_start_at', 'window_end_at', 'samples', 'range_ratio')))
        self.assertEqual((100, 100, 101), tuple(result['memory']['private_bytes'][k]
                                              for k in ('minimum', 'p50', 'maximum')))

    def test_rising_map_tail_is_unknown_with_observed_values(self):
        result = self.observe([(0, 'final-map', 100), (5, 'final-map', 101), (10, 'final-map', 102)])
        self.assertEqual(('unknown', 'still_changing', .02), tuple(result[k] for k in (
            'status', 'reason', 'range_ratio')))
        self.assertEqual(101, result['memory']['private_bytes']['p50'])

    def test_load_time_does_not_turn_a_short_map_hold_into_ten_seconds(self):
        result = self.observe([(0, 'load', 100), (20, 'final-map', 100), (25, 'final-map', 100)])
        self.assertEqual(('unknown', 'insufficient_observation', 5, 0), tuple(result[k] for k in (
            'status', 'reason', 'observed_seconds', 'range_ratio')))
        self.assertEqual(100, result['memory']['private_bytes']['p50'])

    def test_a_missing_map_phase_is_unknown(self):
        result = self.observe([(0, 'load', 100), (60, 'load', 100)])
        self.assertEqual(('unknown', 'frames_phase_missing', {}), tuple(result[k] for k in (
            'status', 'reason', 'memory')))

    def test_truncated_or_invalid_frames_cannot_publish_a_stable_prefix(self):
        self.observe([(0, 'final-map', 100), (10, 'final-map', 100)])
        frames = self.run / 'scene-frames.jsonl'
        with frames.open('a', encoding='utf-8') as source:
            source.write('{"at":')
        self.assertEqual('frames_unreadable_jsonl', self.result()['reason'])
        frames.write_text('{"phase":"final-map","at":"invalid"}\n', encoding='utf-8')
        self.assertEqual('frames_invalid_record', self.result()['reason'])


if __name__ == '__main__':
    unittest.main()
