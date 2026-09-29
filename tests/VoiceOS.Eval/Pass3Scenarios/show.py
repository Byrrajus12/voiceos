#!/usr/bin/env python3
"""Prints per-turn results of a live run dir (or several): show.py <dir> [<dir> ...] [-v]"""
import glob, json, os, re, sys

args = [a for a in sys.argv[1:] if a != "-v"]
verbose = "-v" in sys.argv
for base in args:
    for path in sorted(glob.glob(os.path.join(base, "**", "results.jsonl"), recursive=True)):
        log = os.path.join(os.path.dirname(path), "product.log")
        lines = open(log, encoding="utf-8", errors="replace").read().splitlines() if os.path.exists(log) else []
        for raw in open(path, encoding="utf-8"):
            r = json.loads(raw)
            print(f"{r['ScenarioId']}: {r['Classification']}  {r.get('Note') or ''}")
            for t in r["Turns"]:
                scope = t.get("Scope") or {}
                print(f"  t{t['Index']} [{t.get('Classification')}] {t['Transcript']!r} outcome={t['OutcomeClass']} lane={t['Lane']} scope={json.dumps(scope)[:110]}")
                for c in t.get("Checks", []):
                    if not c.get("Passed", True) or verbose:
                        print(f"      {'ok  ' if c.get('Passed') else 'FAIL'} {c['Name']}: expected={c.get('Expected')} actual={c.get('Actual')}")
                aid = t.get("ActivationId")
                if aid:
                    for l in lines:
                        if aid in l and re.search(r"Referent|Execution scope=|Direct head head=window_target_mode|Browser proof summary|Clarification level", l):
                            print("        · " + re.sub(r"^\S+ \S+ \S+ \[activation_id=\w+\]: ", "", l)[:230])
