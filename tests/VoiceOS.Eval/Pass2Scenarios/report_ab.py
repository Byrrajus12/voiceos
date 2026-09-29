#!/usr/bin/env python3
"""Off-vs-On matched A/B report.

  python report_ab.py <out-dir> [<out-dir>...]

Reads <out>/<Arm>-r<rep>-c<chunk>/<timestamp>/{results.jsonl,product.log} written by run_ab.py.
"""
import sys, os, glob, statistics as st, collections
sys.path.insert(0, os.path.dirname(__file__))
from analyze_proof import load


def arm_of(row):
    return row["run_arm"]


def load_all(outs):
    rows = []
    for out in outs:
        for arm_dir in sorted(glob.glob(os.path.join(out, "*-r*-c*"))):
            if not os.path.isdir(arm_dir):
                continue
            arm = os.path.basename(arm_dir).split("-")[0]
            for run in glob.glob(os.path.join(arm_dir, "*", "results.jsonl")):
                for r in load(os.path.dirname(run)):
                    r["arm"] = arm
                    r["set"] = os.path.basename(out.rstrip("/\\"))
                    rows.append(r)
    return rows


def med(xs):
    xs = [x for x in xs if x is not None]
    return st.median(xs) if xs else None


def mean(xs):
    xs = [x for x in xs if x is not None]
    return st.mean(xs) if xs else None


def fmt(x, d=0):
    return "-" if x is None else f"{x:.{d}f}"


def proof_completed(r):
    s = r["summary"]
    return bool(s) and r["outcome"] == "Complete" and s["first_proved"] != "none" and r["arm"] == "On" and \
        any(p["status"] == "Proved" for p in r["proofs"])


def main(outs):
    rows = load_all(outs)
    arms = collections.defaultdict(list)
    for r in rows:
        arms[r["arm"]].append(r)
    print("turns per arm:", {a: len(v) for a, v in arms.items()})
    classes = ["Pass", "FalseSuccess", "WrongAction", "WrongTarget", "UnnecessaryClarify", "CorrectClarify", "ExecutionFailure", "WrongRoute"]
    print("\n== outcome classes ==")
    for a, v in arms.items():
        c = collections.Counter(r["classification"] for r in v)
        post = sum(1 for r in v if r["classification"] == "UnnecessaryClarify" and r["actions"] > 0)
        zero = sum(1 for r in v if r["classification"] == "UnnecessaryClarify" and r["actions"] == 0 and r["decisions"] > 0)
        print(a, {k: c.get(k, 0) for k in classes}, f"PostActionUnnecessaryClarify={post} ZeroActionBrowserClarify={zero}",
              f"FalseSuccess+WrongAction={c.get('FalseSuccess', 0) + c.get('WrongAction', 0)}")

    print("\n== effort ==")
    for a, v in arms.items():
        act = [r for r in v if r["actions"] > 0]
        print(a, f"turns_with_actions={len(act)} mean_actions={fmt(mean([r['actions'] for r in act]), 2)} "
                 f"mean_browser_model_calls={fmt(mean([r['browser_model_calls'] for r in act]), 2)} "
                 f"median_browser_model_calls={fmt(med([r['browser_model_calls'] for r in act]), 1)} "
                 f"total_browser_model_calls={sum(r['browser_model_calls'] for r in v)}")

    # proved runs in On: compare against the same scenarios' Off runs
    on = arms.get("On", [])
    off = arms.get("Off", [])
    proved = [r for r in on if proof_completed(r) and r["actions"] > 0]
    proved_scen = {r["scenario"] for r in proved}
    off_same = [r for r in off if r["scenario"] in proved_scen and r["outcome"] == "Complete" and r["actions"] > 0]
    print(f"\n== proved runs (On) n={len(proved)} across {len(proved_scen)} scenarios; matched Off runs n={len(off_same)} ==")
    print("calls after last action  On median", fmt(med([r.get('calls_after_last_action') for r in proved]), 1),
          "| Off median", fmt(med([r.get('calls_after_last_action') for r in off_same]), 1))
    print("calls after first action On median", fmt(med([r.get('calls_after_first_action') for r in proved]), 1),
          "| Off median", fmt(med([r.get('calls_after_first_action') for r in off_same]), 1))
    print("last action -> completion ms  On p50", fmt(med([r.get('last_action_to_end_ms') for r in proved])),
          "| Off p50", fmt(med([r.get('last_action_to_end_ms') for r in off_same])))
    print("mean actions  On", fmt(mean([r['actions'] for r in proved]), 2), "| Off", fmt(mean([r['actions'] for r in off_same]), 2))
    print("mean total browser model calls  On", fmt(mean([r['browser_model_calls'] for r in proved]), 2),
          "| Off", fmt(mean([r['browser_model_calls'] for r in off_same]), 2))
    print("post-STT ms p50  On", fmt(med([r['post_stt_ms'] for r in proved])), "| Off", fmt(med([r['post_stt_ms'] for r in off_same])))

    # paired per scenario (median over reps)
    paired = []
    by = collections.defaultdict(lambda: collections.defaultdict(list))
    for r in rows:
        by[r["scenario"]][r["arm"]].append(r)
    for scen, d in by.items():
        if scen in proved_scen and d.get("On") and d.get("Off"):
            def key(rs, k):
                xs = [x.get(k) for x in rs if x["outcome"] == "Complete" and x["actions"] > 0 and x.get(k) is not None]
                return med(xs)
            paired.append((scen, key(d["Off"], "last_action_to_end_ms"), key(d["On"], "last_action_to_end_ms"),
                           key(d["Off"], "calls_after_last_action"), key(d["On"], "calls_after_last_action")))
    tails = [(o - n) for _, o, n, _, _ in paired if o is not None and n is not None]
    calls = [(o - n) for _, _, _, o, n in paired if o is not None and n is not None]
    print(f"paired-by-scenario improvement: tail_ms median {fmt(med(tails))} (n={len(tails)}); calls-after-last median {fmt(med(calls), 1)}")

    print("\n== proof coverage (On) ==")
    on_act = [r for r in on if r["proofs"] and (r["actions"] > 0 or r["family"] == "Surface")]
    on_complete = [r for r in on_act if r["outcome"] == "Complete"]
    print(f"On turns with proof records={len(on_act)}; completed={len(on_complete)}; completed by proof={sum(1 for r in on_complete if proof_completed(r) or (r['summary'] and r['summary']['first_proved'] != 'none' and (r['summary'] or {}).get('rule')))}")
    rules = collections.Counter(r["summary"]["rule"] for r in on if r["summary"] and r["summary"]["first_proved"] != "none")
    print("proved rule distribution:", dict(rules))
    fam = collections.Counter((r["family"], "proved" if (r["summary"] and r["summary"]["first_proved"] != "none") else "not") for r in on if r["proofs"])
    print("family x proved:", dict(fam))

    print("\n== multistep family ==")
    for a, v in arms.items():
        ms = [r for r in v if r["scenario"].startswith("multi.")]
        print(a, f"n={len(ms)} pass={sum(1 for r in ms if r['classification'] == 'Pass')}",
              f"mean_actions={fmt(mean([r['actions'] for r in ms]), 2)}", f"calls={fmt(mean([r['browser_model_calls'] for r in ms]), 2)}")
    print("\n== per-scenario pass counts (Off | On) ==")
    for scen in sorted(by):
        o = by[scen].get("Off", []); n = by[scen].get("On", [])
        po = sum(1 for r in o if r["classification"] == "Pass"); pn = sum(1 for r in n if r["classification"] == "Pass")
        flag = "" if po == pn else "  <-- differs"
        print(f"{scen:34} {po}/{len(o)} | {pn}/{len(n)}{flag}")


if __name__ == "__main__":
    main([a for a in sys.argv[1:] if os.path.isdir(a)])
