#!/usr/bin/env python3
"""Pass 2 proof analysis over live-eval run directories (results.jsonl + product.log).

  python analyze_proof.py <run-dir>... [--json out.json] [--rows]

Per turn it joins the eval record with the product-log lines of its activation id:
effects (identity check), proof records, bind diagnostics, and stage timings
(model calls after the last action, last-action-end -> turn end).
"""
import json, re, sys, statistics as st, collections, os

EFFECT = re.compile(r"Browser effect id=(\S+) kind=(\w+)(?: action=(\S+))?(?: subject=(\S+)@r(\d+)\((\S*)\) tab=(\d+) session=(\S+))?(.*)")
PROOF = re.compile(r"Browser proof mode=(\w+) family=(\w+) after_actions=(\d+) status=(\w+) rule=(\S+) binding=(\S+) detail=(.*?) effects=(\S*)$")
SUMMARY = re.compile(r"Browser proof summary mode=(\w+) family=(\w+) first_proved_actions=(\S+) proved_rule=(\S+) refuted_rule=(\S+) final_completion=(\w+) final_actions=(\d+) final_decisions=(\d+)")
BIND = re.compile(r"Browser bind method=(\w+) (?:top=(\S+) p=([\d.]+) margin=([\d.]+) candidates=(\d+) none=([\d.]+)|target=(\S+) candidates=(\d+))")
AGREE = re.compile(r"Browser bind agreement bound=(\S+) action_kind=(\w+) action_target=(\S*) same=(\w+) click_target_top=(\S+)")
ADOPT = re.compile(r"Browser adopted task tab old=(\d+) new=(\d+)")
FRAMED = re.compile(r"Browser step framed id=\S+ family=(\w+) descriptor_reliable=(\w+)")
ACT = re.compile(r"activation_id=([0-9a-f]+)")
DIAG = re.compile(r"Browser bind diag (.*)")


def load(run_dir):
    rows = []
    log = collections.defaultdict(list)
    with open(os.path.join(run_dir, "product.log"), encoding="utf-8", errors="replace") as f:
        for line in f:
            m = ACT.search(line)
            if m:
                log[m.group(1)].append(line.rstrip("\n"))
    with open(os.path.join(run_dir, "results.jsonl"), encoding="utf-8") as f:
        for line in f:
            r = json.loads(line)
            for t in r["Turns"]:
                rows.append(analyze_turn(run_dir, r, t, log.get(t["ActivationId"], [])))
    return rows


def analyze_turn(run_dir, r, t, lines):
    row = dict(run=os.path.basename(run_dir.rstrip("/\\")), scenario=r["ScenarioId"], attempt=r.get("Attempt", 1),
               transcript=t["Transcript"], classification=t["Classification"], outcome=t.get("ProductOutcome"),
               actions=t["Counts"]["Actions"], decisions=t["Counts"]["BrowserDecisions"], activation=t["ActivationId"],
               post_stt_ms=t["Latency"]["TotalPostSttMs"], family=None, reliable=None, proofs=[], summary=None,
               binds=[], agree=[], effects=[], identity=[], adopted=[], mode=None)
    row["final_title"] = ((t.get("Final") or {}).get("Foreground") or {}).get("Title")
    ends = [c for c in t.get("Checks", []) if c["Category"] == "EndState"]
    row["endstate"] = None if not ends else all(c["Passed"] for c in ends)
    row["endstate_actual"] = [c["Actual"] for c in ends]
    stages = t["Latency"]["Stages"]
    calls = [s for s in stages if s["Name"] in ("browser_decision", "completion_confirmation", "text_value", "normalization")]
    row["browser_model_calls"] = len([s for s in stages if s["Name"] in ("browser_decision", "completion_confirmation", "text_value")])
    acts = [s for s in stages if s["Name"].startswith("action_transport_")]
    if acts:
        last = max(acts, key=lambda s: s["StartMs"])
        end = last["StartMs"] + last["ElapsedMs"]
        row["calls_after_last_action"] = len([s for s in stages if s["Name"] in ("browser_decision", "completion_confirmation", "text_value") and s["StartMs"] >= last["StartMs"] + last["ElapsedMs"] - 1])
        row["last_action_to_end_ms"] = row["post_stt_ms"] - end
        first = min(acts, key=lambda s: s["StartMs"])
        row["calls_after_first_action"] = len([s for s in stages if s["Name"] in ("browser_decision", "completion_confirmation", "text_value") and s["StartMs"] >= first["StartMs"]])
    for l in lines:
        msg = l.split("]: ", 1)[-1] if "]: " in l else l
        if (m := FRAMED.search(msg)): row["family"], row["reliable"] = m.group(1), m.group(2) == "True"
        if (m := PROOF.search(msg)):
            row["mode"] = m.group(1)
            row["proofs"].append(dict(actions=int(m.group(3)), status=m.group(4), rule=m.group(5), binding=m.group(6), detail=m.group(7)))
        if (m := SUMMARY.search(msg)):
            row["summary"] = dict(first_proved=m.group(3), rule=m.group(4), refuted=m.group(5), completion=m.group(6))
        if (m := BIND.search(msg)):
            if m.group(1) == "JevChoice":
                row["binds"].append(dict(method="JevChoice", top=m.group(2), p=float(m.group(3)), margin=float(m.group(4)), n=int(m.group(5)), none=float(m.group(6))))
            else:
                row["binds"].append(dict(method="ExactLabel", top=m.group(7), n=int(m.group(8))))
        if (m := AGREE.search(msg)): row["agree"].append(dict(bound=m.group(1), kind=m.group(2), target=m.group(3), same=m.group(4) == "True"))
        if (m := ADOPT.search(msg)): row["adopted"].append((int(m.group(1)), int(m.group(2))))
        if (m := EFFECT.search(msg)):
            eff = dict(id=m.group(1), kind=m.group(2), action=m.group(3), ref=m.group(4), rev=m.group(5), role=m.group(6),
                       tab=m.group(7), session=m.group(8), rest=m.group(9))
            row["effects"].append(eff)
            if eff["kind"] == "Activated":
                am = re.match(r"r(\d+):click:(\S+)$", eff["action"] or "")
                ok = bool(am) and am.group(1) == eff["rev"] and am.group(2) == eff["ref"] and eff["session"] == t["ActivationId"]
                row["identity"].append(dict(ok=ok, action=eff["action"], ref=eff["ref"], rev=eff["rev"], tab=eff["tab"], session_ok=eff["session"] == t["ActivationId"]))
    return row


def summarize(rows):
    out = {}
    out["turns"] = len(rows)
    ids = [i for r in rows for i in r["identity"]]
    out["activated_effects"] = len(ids)
    out["identity_ok"] = sum(1 for i in ids if i["ok"])
    out["identity_bad"] = [i for i in ids if not i["ok"]]
    out["adopted_events"] = sum(len(r["adopted"]) for r in rows)
    shadow = [r for r in rows if r["proofs"]]
    out["proof_evaluated_turns"] = len(shadow)
    by = collections.Counter()
    for r in shadow:
        by[(r["family"], r["summary"]["first_proved"] != "none" if r["summary"] else False)] += 1
    out["by_family_proved"] = {f"{k[0]}:{'proved' if k[1] else 'not'}": v for k, v in by.items()}
    return out


def main(argv):
    dirs = [a for a in argv if not a.startswith("--") and os.path.isdir(a)]
    rows = []
    for d in dirs:
        rows += load(d)
    summary = summarize(rows)
    print(json.dumps(summary, indent=2))
    if "--rows" in argv:
        for r in rows:
            p = r["summary"]
            print(f'{r["scenario"]:34} {r["classification"]:20} out={r["outcome"]:9} fam={r["family"]} rel={r["reliable"]} act={r["actions"]} dec={r["decisions"]} '
                  f'proved@{p["first_proved"] if p else "-"}:{p["rule"] if p else "-"} refuted={p["refuted"] if p else "-"} '
                  f'bind={[b.get("method")+":"+str(b.get("top"))+":"+str(b.get("p"))+"/"+str(b.get("margin")) for b in r["binds"]][:4]} '
                  f'end={r["endstate"]} title={(r["final_title"] or "")[:40]!r} '
                  f'after_last={r.get("calls_after_last_action")} tail_ms={r.get("last_action_to_end_ms") and round(r["last_action_to_end_ms"])}')
    if "--json" in argv:
        path = argv[argv.index("--json") + 1]
        json.dump(rows, open(path, "w", encoding="utf-8"), indent=1)


if __name__ == "__main__":
    main(sys.argv[1:])
