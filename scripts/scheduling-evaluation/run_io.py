#!/usr/bin/env python3
"""Compare direct and bounded-offload dispatch against a separate HTTP server."""
import argparse
import json
import os
from pathlib import Path
import random
import shutil
import socket
import subprocess
import sys
import time
import urllib.request


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--launches', type=int, default=3)
    parser.add_argument('--batch-control', action='store_true')
    parser.add_argument('--latency-sweep', action='store_true')
    parser.add_argument('--smoke', action='store_true',
                        help='Build and run one small unkeyed direct/offload loopback case.')
    parser.add_argument('--delays', type=int, nargs='+', default=[10, 100, 500])
    parser.add_argument('--measured-requests', type=int, default=8192)
    parser.add_argument('--warmup-requests', type=int, default=4096)
    parser.add_argument('--iterations', type=int, default=3)
    args = parser.parse_args()
    if args.launches < 1:
        parser.error('--launches must be positive')
    for name in ('measured_requests', 'warmup_requests'):
        value = getattr(args, name)
        if value < 1 or value % 1024:
            parser.error(f'--{name.replace("_", "-")} must be a positive multiple of 1024')
    if args.iterations < 1:
        parser.error('--iterations must be positive')
    if args.smoke and (args.batch_control or args.latency_sweep):
        parser.error('--smoke cannot be combined with --batch-control or --latency-sweep')
    if args.smoke:
        args.launches = 1
        args.measured_requests = 1024
        args.warmup_requests = 1024
        args.iterations = 1
    if args.latency_sweep and any(delay < 0 for delay in args.delays):
        parser.error('--delays must contain only non-negative integers')
    output = args.output.resolve()
    scripts = Path(__file__).resolve().parent
    subprocess.run([sys.executable, str(scripts / 'run.py'), '--output', str(output), '--prepare-only'], check=True)
    environment = json.loads((output / 'environment.json').read_text())
    scratch = Path(environment['Scratch'])
    shutil.copy2(scripts / 'IoProgram.cs', scratch / 'Probe/Program.cs')
    server = scratch / 'Server'
    server.mkdir()
    shutil.copy2(scripts / 'IoServer.cs', server / 'Program.cs')
    (server / 'Server.csproj').write_text('''<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
</Project>
''')
    env = os.environ.copy()
    env['DOTNET_TieredCompilation'] = '0'
    for project in ['Probe/Probe.csproj', 'Server/Server.csproj']:
        subprocess.run(['dotnet', 'build', project, '-c', 'Release', '--nologo', '-t:Rebuild'], cwd=scratch, env=env, check=True)
    with socket.socket() as reservation:
        reservation.bind(('127.0.0.1', 0))
        port = reservation.getsockname()[1]
    url = f'http://127.0.0.1:{port}/'
    if args.smoke:
        cases = [('async', 1, 1, 1, 0)]
    elif args.latency_sweep:
        cases = [
            (kind, delay, concurrency, connections, cpu)
            for delay in args.delays
            for kind, concurrency, connections, cpu in [
                ('async', 8, 8, 0),
                ('async', 32, 32, 0),
                ('async', 32, 8, 0),
                ('prefix', 32, 32, 2000000),
            ]
        ]
    else:
        cases = [
            ('async', 1, 8, 8, 0),
            ('async', 10, 8, 8, 0),
            ('async', 50, 8, 8, 0),
            ('async', 10, 32, 32, 0),
            ('async', 10, 32, 8, 0),
            ('prefix', 10, 8, 8, 250000),
            ('prefix', 10, 8, 8, 2000000),
            ('prefix', 10, 32, 32, 2000000),
            ('suffix', 10, 8, 8, 2000000),
            ('suffix', 10, 32, 32, 2000000),
            ('blocking', 10, 8, 8, 0),
            ('blocking', 10, 32, 32, 0),
        ]
    if args.batch_control and not args.latency_sweep:
        cases = [case for case in cases if case[0] == 'async']
    batch_submission = args.batch_control or args.latency_sweep
    probes = []
    for launch in range(args.launches):
        for kind, delay, concurrency, connections, cpu in cases:
            for offload in (False, True):
                probes.append((launch, dict(Kind=kind, DelayMs=delay, Concurrency=concurrency,
                                           Connections=connections, CpuIterations=cpu, Offload=offload,
                                           BatchSubmission=batch_submission,
                                           MeasuredRequests=args.measured_requests,
                                           WarmupRequests=args.warmup_requests,
                                           Iterations=args.iterations)))
    random.Random(3901).shuffle(probes)
    environment.update(Launches=args.launches, Cases=len(cases), BatchSize=32, Partitions=1,
                       Window=1024, MeasuredRequests=args.measured_requests,
                       WarmupRequests=args.warmup_requests, Iterations=args.iterations,
                       LatencySweep=args.latency_sweep,
                       DelaysMs=args.delays if args.latency_sweep else [],
                       Server='Separate Kestrel process; loopback HTTP/1.1',
                       ThreadPoolMinimums='Unchanged', QueueSampleIntervalMs=10,
                       BatchSubmission=batch_submission)
    (output / 'environment.json').write_text(json.dumps(environment, indent=2) + '\n')
    with (output / 'server.log').open('w') as log:
        process = subprocess.Popen(['dotnet', str(server / 'bin/Release/net8.0/Server.dll'), '--urls', url],
                                   cwd=scratch, env=env, stdout=log, stderr=log)
        try:
            for _ in range(100):
                if process.poll() is not None:
                    raise RuntimeError('HTTP server exited; inspect server.log')
                try:
                    with urllib.request.urlopen(url + 'stats', timeout=0.5):
                        break
                except OSError:
                    time.sleep(0.1)
            else:
                raise RuntimeError('HTTP server did not become ready')
            with (output / 'measurements.jsonl').open('w') as stream:
                for index, (launch, settings) in enumerate(probes):
                    result = subprocess.run(['dotnet', str(scratch / 'Probe/bin/Release/net8.0/RequestBatcher.Benchmarks.dll'),
                                             json.dumps(settings), url], cwd=scratch, env=env,
                                            text=True, capture_output=True, timeout=180)
                    if result.returncode:
                        raise RuntimeError(f'{settings}\n{result.stdout}\n{result.stderr}')
                    row = json.loads(result.stdout)
                    row['Launch'] = launch
                    stream.write(json.dumps(row) + '\n')
                    stream.flush()
                    print(f'{index + 1}/{len(probes)} launch={launch} {settings}', flush=True)
        finally:
            process.terminate()
            try:
                process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait()


if __name__ == '__main__':
    main()
