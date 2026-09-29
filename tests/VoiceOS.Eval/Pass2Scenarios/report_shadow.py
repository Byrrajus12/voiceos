#!/usr/bin/env python3
"""Shadow gate report: coverage, agreement, proved-review table, binding calibration.

  python report_shadow.py <run-dir>... [--review] [--calib]
"""
import sys, os, collections
sys.path.insert(0, os.path.dirname(__file__))
from analyze_proof import load

ELIGIBLE = ("Surface", "Find", "Activate")


def proved(r):
    return bool(r["summary"]) and r["summary"]["first_proved"] != "none"


def refuted(r):
    return bool(r["summary"]) and r["summary"]["refuted"] != "-"


def main(argv):
    dirs = [a for a in argv if not a.startswith("--") and os.path.isdir(a)]
    rows = []
    for d in dirs:
        rows += load(d)
    ev = [r for r in rows if r["proofs"]]
    print(f"turns={len(rows)} proof_evaluated={len(ev)} with_actions={sum(1 for r in ev if r['actions'] > 0)}")
    by = collections.Counter(r["family"] for r in ev)
    print("families:", dict(by))

    # eligible = legacy Complete, action-bearing or surface, family in ELIGIBLE
    elig = [r for r in ev if r["family"] in ELIGIBLE and r["outcome"] == "Complete" and (r["actions"] > 0 or r["family"] == "Surface")]
    cov = collections.defaultdict(lambda: [0, 0])
    for r in elig:
        cov[r["family"]][1] += 1
        cov[r["family"]][0] += proved(r)
    tot = [sum(v[0] for v in cov.values()), sum(v[1] for v in cov.values())]
    print(f"coverage (legacy-complete eligible): {tot[0]}/{tot[1]} = {tot[0]/max(tot[1],1):.0%}")
    for f, (p, n) in cov.items():
        print(f"  {f}: {p}/{n} = {p/max(n,1):.0%}")
    # coverage including reach (all legacy-complete action-bearing)
    allc = [r for r in ev if r["outcome"] == "Complete" and (r["actions"] > 0 or r["family"] == "Surface")]
    print(f"coverage over ALL legacy-complete action-bearing incl. Reach: {sum(proved(r) for r in allc)}/{len(allc)}")

    # agreement where both fire
    both = [r for r in ev if proved(r)]
    agree = [r for r in both if r["outcome"] == "Complete"]
    print(f"proved turns={len(both)} legacy also Complete={len(agree)} legacy not Complete={len(both)-len(agree)}")
    print("refuted turns:", [(r["scenario"], r["summary"]["refuted"], r["outcome"]) for r in ev if refuted(r)])

    if "--review" in argv:
        print("\nPROVED REVIEW (scenario | transcript | legacy | classification | rule | end-state | title)")
        for r in both:
            print(f'{r["scenario"]:28} | {r["transcript"][:46]:46} | {r["outcome"]:9} | {r["classification"]:18} | '
                  f'{r["summary"]["rule"]:24} | end={r["endstate"]} {r["endstate_actual"]} | {(r["final_title"] or "")[:44]}')
        print("\nLEGACY-COMPLETE BUT NOT PROVED (eligible)")
        for r in elig:
            if not proved(r):
                last = r["proofs"][-1]
                print(f'{r["scenario"]:28} | {r["transcript"][:40]:40} | fam={r["family"]} rel={r["reliable"]} | '
                      f'last={last["status"]}:{last["rule"]} | rules={sorted({p["rule"] for p in r["proofs"] if p["status"] != "NotYet"})} | end={r["endstate"]}')
        print("\nWRONG/UNSAFE LEGACY CLASSES")
        for r in ev:
            if r["classification"] in ("WrongTarget", "WrongAction", "FalseSuccess"):
                print(r["scenario"], r["classification"], "proved" if proved(r) else "not-proved")

    if "--calib" in argv:
        print("\nBIND CALIBRATION (bound == executed action; truth = end-state check)")
        buckets = collections.defaultdict(lambda: [0, 0])
        rows_c = []
        for r in ev:
            if r["endstate"] is None or not r["agree"]:
                continue
            # the LAST agreeing bind before the action that produced the final state approximates the deciding bind
            for b, a in zip([b for b in r["binds"] if b["method"] == "JevChoice" and b["top"] != "NONE"], r["agree"]):
                pass
            jb = [b for b in r["binds"] if b["method"] == "JevChoice" and b["top"] != "NONE"]
            if not jb:
                continue
            b = jb[-1]
            same = r["agree"][-1]["same"] if r["agree"] else False
            rows_c.append((b["p"], b["margin"], same, r["endstate"], r["scenario"]))
        for p, m, same, ok, sc in sorted(rows_c, reverse=True):
            print(f"p={p:.2f} margin={m:.2f} same_as_action={same} target_correct(end-state)={ok} {sc}")


if __name__ == "__main__":
    main(sys.argv[1:])
