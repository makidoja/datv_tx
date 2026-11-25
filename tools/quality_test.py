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
RATES = [250, 375, 600]
VARIANTS = ["S", "I", "IR", "IRT", "IRTF", "IRTFD"]
LEGEND = {
    "S": "current (CBR: minrate=maxrate=bufsize)",
    "I": "VBV only: maxrate, bufsize 2x, no minrate",
    "R": "auto resolution (640x360 <300k, 960x540 <=800k)",
    "T": "x265 tuning: lookahead 30, aq-mode 3, deblock 1:1, b-adapt 2",
    "F": "intra refresh instead of keyframes",
    "D": "hqdn3d light denoise",
}

def run(cmd):
    return subprocess.run(cmd, shell=True, capture_output=True, text=True)

def size_for(v, name):
    if "R" not in name: return "1280x720"
    return "640x360" if v < 300 else "960x540" if v <= 800 else "1280x720"

def encode(src, out, v, name):
    vf = []
    if "D" in name: vf.append("hqdn3d=3:2:4:3")
    vf.append("scale=" + size_for(v, name) + ":flags=bicubic")
    if "I" in name: rc = f"-b:v {v}K -maxrate {v}K -bufsize {2*v}K"
    else: rc = f"-b:v {v}K -minrate {v}K -maxrate {v}K -bufsize {v}K"
    xp = ["log-level=error"]
    if "T" in name: xp += ["rc-lookahead=30", "aq-mode=3", "deblock=1,1", "b-adapt=2"]
    if "F" in name: xp += ["intra-refresh=1", "keyint=75"]
    cmd = (f'{FF} -hide_banner -loglevel error -y -i {src} -vf {",".join(vf)} -pix_fmt yuv420p '
           f'-c:v libx265 -preset veryfast -r 15 {rc} -g 75 -x265-params {":".join(xp)} -f mpegts {out}')
    t = time.time(); r = run(cmd); dt = time.time() - t
    if r.returncode: raise RuntimeError(r.stderr[-800:])
    return dt

def score(ref, enc):
    log = enc + ".json"
    lavfi = (f"[0:v]scale=1280x720:flags=bicubic,setpts=PTS-STARTPTS[d];[1:v]setpts=PTS-STARTPTS[r];"
             f"[d][r]libvmaf=log_fmt=json:log_path={log}:n_threads=4")
    r = run(f'{FF} -hide_banner -i {enc} -i {ref} -lavfi "{lavfi}" -f null -')
    if r.returncode: raise RuntimeError(r.stderr[-800:])
    frames = [f["metrics"]["vmaf"] for f in json.load(open(log))["frames"]]
    frames.sort()
    worst = statistics.mean(frames[: max(1, len(frames) // 20)])
    return statistics.mean(frames), worst

print("Variants:"); [print(f"  {k}: {v}") for k, v in LEGEND.items()]
for clip in CLIPS:
    ref = clip
    print(f"\n=== {os.path.basename(clip)} ===")
    print(f"{'kb/s':>5} {'variant':8} {'VMAF':>6} {'worst5%':>8} {'enc fps':>8}")
    for v in RATES:
        for name in VARIANTS:
            out = f"{clip}.{v}.{name}.ts"
            try:
                dt = encode(ref, out, v, name)
                mean, worst = score(ref, out)
                print(f"{v:5} {name:8} {mean:6.1f} {worst:8.1f} {150/dt:8.1f}", flush=True)
            except Exception as e:
                print(f"{v:5} {name:8} ERROR {e}", flush=True)
