#!/usr/bin/env python3
"""Bounded offline resource evidence, using already installed psutil only.

Sampling is not a kernel memory limit or a GPU/network measurement. This tool
owns only its child processes; it never attaches to or terminates MSFS/the app.
"""
import argparse
import datetime as dt
import os
from pathlib import Path
import platform
import shutil
import subprocess
import time

import p08c as evidence

# Conservative test budgets for the documented 2-CPU/8-GiB Linux worker and
# bounded retained inputs, not universal aircraft/data-size guarantees.
BUDGETS = {'wall_seconds': 300, 'sampled_tree_rss_bytes': 512*1024**2,
           'output_bytes': 256*1024**2, 'minimum_free_bytes': 256*1024**2}


def file_bytes(root):
    total = 0
    for count, path in enumerate(root.rglob('*')):
        evidence.require(count < 20000, 'Output file/directory count budget exceeded.')
        evidence.safe_path(path)
        if path.is_file():
            total += path.stat().st_size
    return total


def measure(command, output, label, budgets=None, interrupt_after=None):
    """Sample the owned child tree. Short-lived children can escape sampling."""
    try:
        import psutil
    except ImportError:
        raise ValueError('Resource measurement blocked: psutil is not already installed. Nothing was installed or launched.') from None
    budgets = dict(budgets or BUDGETS)
    evidence.require(shutil.disk_usage(output).free >= budgets['minimum_free_bytes'], 'Insufficient free disk; keep earlier evidence.')
    start_bytes = file_bytes(output)
    started = time.monotonic(); peak = 0; peak_growth = 0; cpu = {}; reads = {}; writes = {}; reason = None; samples = 0
    io_unavailable = False; previous_sample = started; largest_interval = 0
    kernel_file = output/(label+'-kernel.json')
    # If GNU time is already available, it measures reaped
    # child CPU and the largest individual RSS high-water mark (not tree sum).
    kernel_time = Path('/usr/bin/time')
    if platform.system() == 'Linux' and kernel_time.is_file():
        command = [kernel_time, '-f', '{"user_seconds":%U,"system_seconds":%S,"largest_process_rss_kib":%M,"exit_code":%x}',
                   '-o', kernel_file, '--', *command]
    with (output/(label+'.log')).open('xb') as log:
        process = subprocess.Popen([str(v) for v in command], cwd=evidence.ROOT, stdout=log, stderr=subprocess.STDOUT,
                                   env={**os.environ, 'LC_ALL': 'C'})
        parent = psutil.Process(process.pid)
        owned = {process.pid: parent}
        try:
            while process.poll() is None:
                elapsed = time.monotonic()-started
                try:
                    for child in parent.children(recursive=True): owned[child.pid] = child
                except psutil.NoSuchProcess:
                    pass
                evidence.require(len(owned) <= 128, 'Unexpectedly large owned process tree.')
                rss = 0
                for child in list(owned.values()):
                    try:
                        with child.oneshot():
                            key = (child.pid, child.create_time())
                            rss += child.memory_info().rss
                            times = child.cpu_times(); cpu[key] = times.user + times.system
                            try:
                                io = child.io_counters(); reads[key] = io.read_bytes; writes[key] = io.write_bytes
                            except psutil.AccessDenied:
                                # Disk counters are informational, never a zero-byte
                                # claim when access is unavailable. RSS/CPU failures
                                # still stop the bounded benchmark.
                                io_unavailable = True
                    except psutil.NoSuchProcess:
                        pass
                peak = max(peak, rss); samples += 1
                sampled_at = time.monotonic(); largest_interval = max(largest_interval, sampled_at-previous_sample); previous_sample = sampled_at
                growth = file_bytes(output)-start_bytes; peak_growth = max(peak_growth, growth)
                if interrupt_after is not None and elapsed >= interrupt_after: reason = 'explicit-disposable-interruption'
                elif elapsed > budgets['wall_seconds']: reason = 'wall-budget'
                elif rss > budgets['sampled_tree_rss_bytes']: reason = 'sampled-memory-budget'
                elif growth > budgets['output_bytes']: reason = 'output-budget'
                elif shutil.disk_usage(output).free < budgets['minimum_free_bytes']: reason = 'free-disk-budget'
                if reason:
                    break
                time.sleep(.05)
        finally:
            # PID reuse is checked by psutil's process identity. Only children
            # observed under this Popen are eligible for cancellation.
            for child in reversed(list(owned.values())):
                try:
                    if child.is_running(): child.kill()
                except psutil.NoSuchProcess:
                    pass
            process.wait(timeout=10)
    result = {'exit_code': process.returncode, 'stop_reason': reason,
        'wall_seconds': time.monotonic()-started, 'sample_count': samples,
        'sampled_process_tree_peak_rss_bytes': peak, 'sampled_process_tree_cpu_seconds': sum(cpu.values()),
        'sampled_process_read_bytes': None if io_unavailable else sum(reads.values()),
        'sampled_process_write_bytes': None if io_unavailable else sum(writes.values()),
        'disk_counter_status': 'Unavailable for at least one process; not zero' if io_unavailable else 'Sampled OS process counters',
        'final_output_growth_bytes': file_bytes(output)-start_bytes,
        'sampled_peak_output_growth_bytes': peak_growth,
        'psutil_version': psutil.__version__, 'os': platform.platform(),
        'logical_cpus': os.cpu_count(), 'physical_memory_bytes': psutil.virtual_memory().total,
        'nominal_sampling_sleep_seconds': .05, 'largest_observed_sampling_interval_seconds': largest_interval, 'budgets': budgets,
        'kernel_reaped_child_metrics': None,
        'limitations': ['Sampled RSS/CPU/IO can miss short-lived children and spikes; not an OS hard limit.',
            'IO counters are OS process disk counters, not HTTP payload or network bytes.',
            'No native GPU/frame-rate measurement; no installed Windows/MSFS acceptance.']}
    if reason is None and process.returncode == 0 and kernel_file.exists():
        result['kernel_reaped_child_metrics'] = evidence.read(kernel_file)
        result['kernel_time_executable_sha256'] = evidence.digest(kernel_time)
    evidence.write_new(output/(label+'-metrics.json'), result)
    evidence.require(reason is None and process.returncode == 0, label+' stopped/failed. Preserve metrics and log.')
    evidence.require(result['final_output_growth_bytes'] <= budgets['output_bytes'], 'Final output budget exceeded.')
    return result


def canonical(value):
    if isinstance(value, list): return [canonical(v) for v in value]
    if isinstance(value, dict): return {k[:1].lower()+k[1:]: canonical(v) for k,v in value.items()}
    return value


def retained_inventory(seed):
    seed = evidence.safe_path(seed)
    probe = canonical(evidence.read(seed/'probe.json'))
    evidence.require(probe.get('syntheticFlight') is True and probe.get('genuineHistoricalSources') is True,
                     'Only an explicitly isolated genuine-source probe is eligible.')
    relative = probe['sourceLibrary']
    evidence.require(not Path(relative).is_absolute() and '..' not in Path(relative).parts and ':' not in relative,
                     'Invalid isolated Library path.')
    sources = canonical(evidence.read(seed/'sources.json'))
    evidence.require(1 <= len(sources) <= 64, 'Use at most 64 previously retained source originals.')
    items = []
    for source in sources:
        manifest = source['manifest']; cloud = manifest['pointCloud']; original = cloud['originals'][0]
        evidence.require(len(cloud['originals']) == 1 and manifest['sceneId'].startswith('IA_FullState/'),
                         'This retained probe is restricted to its previously approved project.')
        header = original['header']
        file = next(f for f in source['files'] if f['relativePath'] == original['file'])
        path = evidence.safe_path(seed/relative/source['relativeDirectory']/file['relativePath'])
        evidence.require(path.is_relative_to(seed), 'Original path escapes the disposable seed.')
        items.append({'id': source['id'], 'path': str(path), 'kind': 'ept-node', 'provider_project': manifest['sceneId'],
            'source_date_utc': manifest['observationUtc'], 'horizontal_crs': header['horizontalCrs'],
            'vertical_datum': header['verticalReference'], 'units': header['verticalUnit'],
            'native_shape_or_count': header['pointCount'], 'attributes': header,
            'license_access': manifest['license'], 'retained_authorization': 'Previously approved P05c retained probe; offline reuse only',
            'expected_sha256': file['sha256']})
    items = evidence.inventory({'schema': 'aeroscience-p08c-inputs-1', 'inputs': items})
    evidence.require(sum(v['native_shape_or_count'] for v in items) <= 2000000, 'Retained candidate-point limit exceeded.')
    return items


def run(args):
    seed = evidence.safe_path(args.seed)
    inputs = retained_inventory(seed)
    evidence.require(file_bytes(seed) < BUDGETS['output_bytes']//2, 'Seed too large for this disposable-copy budget.')
    runtime = evidence.identity(args.host)
    output = evidence.fresh(args.output)
    plan = {'schema': evidence.SCHEMA, 'evidence_kind': 'resource-benchmark', 'operation': 'retained-lidar',
        'runtime': runtime, 'inputs': inputs, 'historical_sources': True, 'synthetic_flight': True,
        'budgets': BUDGETS, 'tolerances': evidence.TOLERANCES['lidar'],
        'candidate_points': sum(v['native_shape_or_count'] for v in inputs),
        'original_bytes': sum(v['bytes'] for v in inputs), 'network_transfer_bytes': 0,
        'network_scope': 'Existing offline retained probe; no OS network capture',
        'installed_acceptance': 'Not Tested', 'harness_sha256': evidence.digest(__file__),
        'started_utc': dt.datetime.now(dt.timezone.utc).isoformat()}
    evidence.write_new(output/'plan.json', plan)
    receipt = {**plan, 'outcome': 'Failed'}
    try:
        # Retained processing adds jobs/checkpoints: NEVER pass the original seed
        # to the mutable probe. All work is on a fresh bounded disposable copy.
        shutil.copytree(seed, output/'disposable-seed')
        receipt['application_metrics'] = measure([args.dotnet,args.host,'--lidar-retained-probe',
            output/'disposable-seed',output/'probe'], output, 'application')
        oracle = evidence.ROOT/'scripts/validation/verify-lidar-points.py'
        receipt['reference_metrics'] = measure([args.reference_python,'-I',oracle,output/'probe'],output,'independent-reference')
        reference = evidence.read(output/'probe/independent-comparison.json')
        evidence.require(reference['state'] == 'Passed', 'Independent point comparison failed.')
        evidence.require(all(evidence.digest(v['path']) == v['sha256'] for v in inputs), 'Retained seed original changed.')
        receipt.update(outcome='Passed', independent_reference=reference, oracle_sha256=evidence.digest(oracle))
    except Exception as exc:
        receipt['error'] = str(exc); raise
    finally:
        receipt['finished_utc'] = dt.datetime.now(dt.timezone.utc).isoformat()
        receipt['artifacts'] = evidence.artifact_records(output)
        evidence.write_new(output/'receipt.json', receipt)
    return output/'receipt.json'


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--seed', type=Path, required=True); parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--host', type=Path, required=True); parser.add_argument('--reference-python', type=Path, required=True)
    parser.add_argument('--dotnet', default=os.environ.get('DOTNET','dotnet'))
    args = parser.parse_args()
    try: print(run(args))
    except (ValueError,OSError,subprocess.SubprocessError,KeyError) as exc:
        parser.exit(1, 'P08c resources refused: '+str(exc)+'\n')


if __name__ == '__main__': main()
