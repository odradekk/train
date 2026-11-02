"""从 scene-soak 原始证据重新判定内存，不读取旧 assessment。"""
from math import isfinite
from statistics import median

MIB = 1048576


def number(value):
    return type(value) in (int, float) and isfinite(value)


def at(value, *keys):
    for key in keys:
        value = value.get(key) if isinstance(value, dict) else None
    return value


def check(value, threshold, rule, reason=None, unit='bytes', recorded=False):
    known = number(value)
    result = {'status': 'unknown' if reason or not known else 'recorded' if recorded else
              'failed' if value > threshold else 'passed', 'unit': unit, 'rule': rule,
              'measured': value if known else None, 'reason': reason or (None if known else 'missing_or_invalid_evidence')}
    if not recorded:
        result.update(threshold=threshold, excess=max(0, value - threshold) if known and not reason else None)
    return result


def heap_delta(soak, reason):
    rows = at(soak, 'memory', 'reset_heaps')
    segments = at(soak, 'segments')
    seconds = at(soak, 'seconds')
    resets = [row for row in rows if isinstance(row, dict) and row.get('phase') == 'reset'] if isinstance(rows, list) else []
    valid = (isinstance(rows, list) and type(segments) is int and segments >= 2 and
             all(isinstance(row, dict) and row.get('phase') in ('reset', 'final') for row in rows) and
             [row.get('segment') for row in resets] == list(range(1, segments + 1)) and
             all(type(row.get('segment')) is int for row in resets) and
             all(number(row.get('live_bytes')) and row['live_bytes'] >= 0 for row in resets))
    problem = reason or ('observation_shorter_than_two_hours' if not number(seconds) or seconds < 7200 else
                         None if valid else 'reset_heap_segments_missing_or_invalid')
    value = max(row['live_bytes'] - resets[0]['live_bytes'] for row in resets) if valid else None
    return check(value, 100 * MIB, 'maximum_reset_heap_minus_first_reset', problem)


def absolute_memory(identity, records, reason):
    budget = at(identity, 'scene_budget')
    samples = [at(budget, key) for key in ('initial_map', 'editor_steady', 'after_editor_map', 'final_map')
               if isinstance(at(budget, key), dict)]
    if isinstance(at(budget, 'editor_sequence', 'after'), dict):
        samples.append(at(budget, 'editor_sequence', 'after'))
    samples.extend(at(row, 'steady' if row.get('kind') == 'open' else 'memory')
                   for row in records if row.get('kind') in ('open', 'natural-map'))
    platform = at(identity, 'os')
    linux = isinstance(platform, str) and 'linux' in platform.lower()
    windows = isinstance(platform, str) and 'windows' in platform.lower()
    results = {}
    for key, counter, limit in (('game_private', 'private_bytes', 4096 * MIB),
                                ('game_working_set', 'working_set_bytes', 3072 * MIB),
                                ('game_render', 'video_bytes', 2048 * MIB)):
        values = [at(sample, counter) for sample in samples]
        valid = bool(samples) and all(number(at(sample, 'samples')) and at(sample, 'samples') > 0 and
                    number(value) and value >= 0 for sample, value in zip(samples, values))
        problem = reason or (None if valid else 'steady_counter_missing_or_invalid')
        working = key == 'game_working_set'
        if working and not (linux or windows): problem = problem or 'platform_missing_or_unsupported'
        results[key] = check(max(values) if valid else None, limit,
                             'maximum_recorded_steady_median', problem, recorded=working and linux)
    return results


def combine(items, threshold, rule, statistic=max, unit='bytes'):
    values = [item['measured'] for item in items if item['status'] in ('passed', 'failed', 'recorded')]
    unknown = len(items) - len(values)
    value = statistic(values) if values else None
    reason = 'unknown_runs' if unknown else 'insufficient_valid_runs' if len(values) < 3 else None
    result = check(value, threshold, rule, reason, unit)
    # 已知超限足以否定通过；缺失证据仍保留在 reason 和计数中。
    if number(value) and value > threshold and len(values) >= 3:
        result['status'] = 'failed'
        result['excess'] = value - threshold
    result.update(valid_runs=len(values), unknown_runs=unknown)
    return result


def summarize_memory(reports, operation_runs, hourly):
    runs = []
    for index, ((report, _, source_problem), (records, operation_problem)) in enumerate(zip(reports, operation_runs), 1):
        reason = source_problem or operation_problem
        identity = report.get('memory_identity')
        hour = next((item for item in hourly['runs'][index - 1]['hours'] if item['hour'] == 2), {})
        seconds = at(identity, 'scene_budget', 'soak', 'seconds')
        growth_problem = 'observation_shorter_than_two_hours' if not number(seconds) or seconds < 7200 else None
        growth = check(hour.get('private_bytes_per_hour'), 200 * MIB, 'second_hour_private_ols',
                       reason or growth_problem or hourly['runs'][index - 1]['reason'] or hour.get('reason'), 'bytes/hour')
        runs.append({'run': index, 'retired_heap_delta': heap_delta(at(identity, 'scene_budget', 'soak'), reason),
                     'private_growth': growth, **absolute_memory(identity, records, reason)})
    result = {'runs': runs}
    platforms = [at(report.get('memory_identity'), 'os') for report, _, _ in reports]
    has_linux = any(isinstance(platform, str) and 'linux' in platform.lower() for platform in platforms)
    has_windows = any(isinstance(platform, str) and 'windows' in platform.lower() for platform in platforms)
    for key, threshold in (('retired_heap_delta', 100 * MIB), ('game_private', 4096 * MIB),
                           ('game_working_set', 3072 * MIB), ('game_render', 2048 * MIB)):
        items = [run[key] for run in runs]
        if key == 'game_working_set' and has_linux:
            values = [item['measured'] for item in items if item['status'] in ('recorded', 'passed', 'failed')]
            unknown = len(items) - len(values)
            reason = ('mixed_platforms' if has_windows else 'unknown_runs' if unknown else
                      'insufficient_valid_runs' if len(values) < 3 else None)
            result[key] = check(max(values) if values else None, threshold,
                                'maximum_across_runs', reason, recorded=True)
            result[key].update(valid_runs=len(values), unknown_runs=unknown)
        else:
            result[key] = combine(items, threshold, 'maximum_across_runs')
    items = [run['private_growth'] for run in runs]
    result['private_growth_median'] = combine(items, 100 * MIB, 'median_second_hour_private_ols',
                                            median, 'bytes/hour')
    result['private_growth_maximum'] = combine(items, 200 * MIB, 'maximum_second_hour_private_ols',
                                             max, 'bytes/hour')
    return result
