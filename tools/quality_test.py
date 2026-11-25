#!/usr/bin/env python3
"""Compare DATV picture-quality settings with VMAF on real test footage.

Each clip is cut to 10 s at 15 fps 1280x720 (the reference), encoded with every
variant at several video bit rates, decoded, scaled back to 1280x720 and scored
against the reference. 'worst' is the mean VMAF of the worst 5% of frames,
which is where blocking on motion shows up.
"""
import json, os, statistics, subprocess, sys, time

FF = sys.argv[1] if len(sys.argv) > 1 else "ffmpeg"
CLIPS = sys.argv[2].split(",") if len(sys.argv) > 2 else []
RATES = [375, 600]
# Round 3: smooth motion. Source is the clip at 30 fps; each variant makes its own 10/15 fps
# reference (plain drop vs blended), and the encode is scored against that reference, so the
# score shows how cleanly the encoder reproduced it (blocking), not the blur itself.
VARIANTS = {
    "D10 10 fps, frames dropped":  dict(fps=10, pre="fps=10"),
    "B10 10 fps, frames blended":  dict(fps=10, pre="fps=30,tmix=frames=3,fps=10"),
    "D15 15 fps, frames dropped":  dict(fps=15, pre="fps=15"),
    "B15 15 fps, frames blended":  dict(fps=15, pre="fps=30,tmix=frames=2,fps=15"),
}

def run(cmd):
    return subprocess.run(cmd, shell=True, capture_output=True, text=True)

def encode(src, out, v, spec):
    fps = spec["fps"]
    ref = out + ".ref.mkv"
    r = run(f'{FF} -hide_banner -loglevel error -y -i {src} -vf {spec["pre"]},format=yuv420p -c:v ffv1 {ref}')
    if r.returncode: raise RuntimeError(r.stderr[-800:])
    rc = f"-b:v {v}K -maxrate {v}K -bufsize {v}K"
    cmd = (f'{FF} -hide_banner -loglevel error -y -i {ref} -pix_fmt yuv420p -c:v libx265 -preset veryfast '
           f'-r {fps} {rc} -g {5*fps} -x265-params log-level=error -f mpegts {out}')
    t = time.time(); r = run(cmd); dt = time.time() - t
    if r.returncode: raise RuntimeError(r.stderr[-800:])
    return dt, ref

def score(ref, enc):
    log = enc + ".json"
    lavfi = (f"[0:v]setpts=PTS-STARTPTS[d];[1:v]setpts=PTS-STARTPTS[r];"
             f"[d][r]libvmaf=log_fmt=json:log_path={log}:n_threads=4")
    r = run(f'{FF} -hide_banner -i {enc} -i {ref} -lavfi "{lavfi}" -f null -')
    if r.returncode: raise RuntimeError(r.stderr[-800:])
    frames = [f["metrics"]["vmaf"] for f in json.load(open(log))["frames"]]
    frames.sort()
    worst = statistics.mean(frames[: max(1, len(frames) // 20)])
    return statistics.mean(frames), worst

for clip in CLIPS:
    print(f"\n=== {os.path.basename(clip)} ===")
    print(f"{'kb/s':>5} {'variant':32} {'VMAF':>6} {'worst5%':>8} {'enc fps':>8}")
    for v in RATES:
        for name, spec in VARIANTS.items():
            out = f"{clip}.{v}.{name.split()[0]}.ts"
            try:
                dt, ref = encode(clip, out, v, spec)
                mean, worst = score(ref, out)
                print(f"{v:5} {name:32} {mean:6.1f} {worst:8.1f} {150/dt:8.1f}", flush=True)
            except Exception as e:
                print(f"{v:5} {name:32} ERROR {e}", flush=True)
