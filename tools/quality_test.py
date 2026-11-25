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
# Round 2: every variant uses VBV (maxrate, bufsize 2x, no minrate) at 1280x720 15 fps unless noted.
VARIANTS = {
    "S  current CBR, veryfast":      dict(cbr=True),
    "I  VBV, veryfast":              dict(),
    "I0 VBV, veryfast, no B-frames": dict(xp=["bframes=0"]),
    "If VBV, fast":                  dict(preset="fast"),
    "Im VBV, medium":                dict(preset="medium"),
    "Is VBV, slow":                  dict(preset="slow"),
    "Ig VBV, veryfast, GOP 10 s":    dict(gop=150),
    "Ir VBV, veryfast, 10 fps":      dict(fps=10),
    "Mr VBV, medium, 10 fps":        dict(preset="medium", fps=10),
    "Mq VBV, medium, 960x540":       dict(preset="medium", size="960x540"),
    "A  SVT-AV1 preset 8 CBR":       dict(codec="libsvtav1", preset="8"),
    "H  x264 VBV, medium":           dict(codec="libx264", preset="medium"),
}

def run(cmd):
    return subprocess.run(cmd, shell=True, capture_output=True, text=True)

def encode(src, out, v, spec):
    codec = spec.get("codec", "libx265"); preset = spec.get("preset", "veryfast")
    fps = spec.get("fps", 15); gop = spec.get("gop", 5 * fps); size = spec.get("size", "1280x720")
    vf = "scale=" + size + ":flags=bicubic"
    if fps != 15: vf = "fps=" + str(fps) + "," + vf
    if spec.get("cbr"): rc = f"-b:v {v}K -minrate {v}K -maxrate {v}K -bufsize {v}K"
    else: rc = f"-b:v {v}K -maxrate {v}K -bufsize {2*v}K"
    extra = ""
    if codec == "libx265":
        extra = "-x265-params " + ":".join(["log-level=error"] + spec.get("xp", []))
    elif codec == "libsvtav1":
        rc = f"-b:v {v}K"
        extra = "-svtav1-params tune=0:rc=2:pred-struct=1"
    cmd = (f'{FF} -hide_banner -loglevel error -y -i {src} -vf {vf} -pix_fmt yuv420p '
           f'-c:v {codec} -preset {preset} {rc} -g {gop} {extra} -f mpegts {out}')
    t = time.time(); r = run(cmd); dt = time.time() - t
    if r.returncode: raise RuntimeError(r.stderr[-800:])
    return dt

def score(ref, enc):
    log = enc + ".json"
    lavfi = (f"[0:v]fps=15,scale=1280x720:flags=bicubic,setpts=PTS-STARTPTS[d];[1:v]setpts=PTS-STARTPTS[r];"
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
                dt = encode(clip, out, v, spec)
                mean, worst = score(clip, out)
                print(f"{v:5} {name:32} {mean:6.1f} {worst:8.1f} {150/dt:8.1f}", flush=True)
            except Exception as e:
                print(f"{v:5} {name:32} ERROR {e}", flush=True)
