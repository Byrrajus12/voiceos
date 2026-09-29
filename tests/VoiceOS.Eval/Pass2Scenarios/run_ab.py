#!/usr/bin/env python3
"""Interleaved proof Off-vs-On live A/B driver.

  python run_ab.py --out artifacts/eval/p5 --reps 2 --chunk 5 [--tag pass2] [--scenario id]... [--scenarios-dir dir]

Same build for both arms; only VOICEOS_BROWSER_PROOF differs. Scenarios are split into chunks and each
chunk runs once per arm, alternating which arm goes first (by chunk and repetition) so provider or
network drift does not align with an arm. Each run writes to <out>/<arm>-r<rep>-c<chunk>/.
"""
import argparse, os, subprocess, sys, json, itertools

EVAL = os.path.join("tests", "VoiceOS.Eval", "bin", "Debug", "net9.0-windows10.0.19041.0", "VoiceOS.Eval.dll")


def list_ids(scenarios_dir, tags, ids):
    if ids:
        return ids
    out = subprocess.run(["dotnet", EVAL, "live", "--scenarios", scenarios_dir, *sum((["--tag", t] for t in tags), []), "--list"],
                         capture_output=True, text=True).stdout
    return [line.split()[0] for line in out.splitlines() if "family=" in line]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", required=True)
    ap.add_argument("--reps", type=int, default=2)
    ap.add_argument("--chunk", type=int, default=5)
    ap.add_argument("--tag", action="append", default=[])
    ap.add_argument("--scenario", action="append", default=[])
    ap.add_argument("--scenarios-dir", default=os.path.join("tests", "VoiceOS.Eval", "Pass2Scenarios"))
    ap.add_argument("--arms", default="Off,On")
    args = ap.parse_args()
    arms = args.arms.split(",")
    ids = list_ids(args.scenarios_dir, args.tag, args.scenario)
    chunks = [ids[i:i + args.chunk] for i in range(0, len(ids), args.chunk)]
    os.makedirs(args.out, exist_ok=True)
    plan = []
    for rep in range(args.reps):
        for ci, chunk in enumerate(chunks):
            order = arms if (rep + ci) % 2 == 0 else list(reversed(arms))
            for arm in order:
                plan.append((arm, rep, ci, chunk))
    json.dump([dict(arm=a, rep=r, chunk=c, ids=ch) for a, r, c, ch in plan], open(os.path.join(args.out, "plan.json"), "w"), indent=1)
    for arm, rep, ci, chunk in plan:
        out = os.path.join(args.out, f"{arm}-r{rep}-c{ci}")
        if os.path.exists(os.path.join(out, "done")):
            continue
        env = dict(os.environ, VOICEOS_BROWSER_PROOF=arm, VOICEOS_EVAL_PROOF_DIAG="1")
        cmd = ["dotnet", EVAL, "live", "--scenarios", args.scenarios_dir, "--out", out]
        for sid in chunk:
            cmd += ["--scenario", sid]
        print(f"[{arm} rep{rep} chunk{ci}] {len(chunk)} scenarios", flush=True)
        subprocess.run(cmd, env=env, stdout=open(out + ".log", "w"), stderr=subprocess.STDOUT)
        os.makedirs(out, exist_ok=True)
        open(os.path.join(out, "done"), "w").close()


if __name__ == "__main__":
    main()
