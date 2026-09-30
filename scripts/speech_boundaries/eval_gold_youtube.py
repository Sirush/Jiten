"""Score the punctuation labels, the rule and models against the hand-labelled YouTube gold set.

    python scripts/speech_boundaries/eval_gold_youtube.py youtube_breaks.tsv gold_youtube_dir model_a.json [model_b.json ...]
"""
import glob
import json
import os
import sys

import numpy as np
import pandas as pd

from eval_youtube import predict
from train import load, report, rule_predict

sys.stdout.reconfigure(encoding="utf-8")


def main(tsv_path: str, gold_dir: str, model_paths: list[str]) -> None:
    unlabelled = {r["id"]: r for r in map(json.loads, open(os.path.join(gold_dir, "gold_unlabelled.jsonl"), encoding="utf-8"))}
    gold = {}
    for path in sorted(glob.glob(os.path.join(gold_dir, "gold_labels_*.jsonl"))):
        for line in open(path, encoding="utf-8"):
            if line.strip():
                r = json.loads(line)
                gold[r["id"]] = r
    print(f"{len(gold)} gold labels, {len(set(unlabelled) - set(gold))} missing")

    groups = {u["group"] for u in unlabelled.values()}
    df = load(tsv_path)
    df = df[df["group"].isin(groups)].reset_index(drop=True)
    df["rule"] = rule_predict(df)
    for path in model_paths:
        df[path] = predict(df, path)

    key = df.set_index(["group", "file", "idx", "next_idx"])
    rows = []
    for i, g in gold.items():
        u = unlabelled[i]
        t = key.loc[(u["group"], u["file"], u["idx"], u["next_idx"])]
        rows.append({"id": i, "group": u["group"], "gold": g["label"], "sure": g["sure"], "auto": int(t["label"]),
                     "rule": int(t["rule"]), **{p: float(t[p]) for p in model_paths}, "prev": u["prev"], "next": u["next"]})
    ev = pd.DataFrame(rows)

    for name, subset in (("all", ev), ("sure only", ev[ev["sure"]])):
        y = subset["gold"].to_numpy()
        print(f"\n{name}: {len(subset)} breaks, {y.mean():.1%} boundaries")
        report("punct labels", y, subset["auto"].to_numpy())
        report("every break", y, np.ones_like(y))
        report("rule", y, subset["rule"].to_numpy())
        for path in model_paths:
            for threshold in (0.5, 0.6, 0.7):
                report(f"{os.path.basename(path)[:12]}@{threshold}", y, (subset[path] >= threshold).astype(int).to_numpy(),
                       subset[path].to_numpy())
            prob = subset[path].to_numpy()
            for margin in (0.1, 0.2):
                confident = (prob >= 1 - margin) | (prob <= margin)
                acc = ((prob[confident] >= 0.5).astype(int) == y[confident]).mean() if confident.any() else float("nan")
                print(f"  confident (p <= {margin} or >= {1 - margin}): {confident.mean():.1%} of breaks, acc {acc:.4f}")

    ev.to_csv(os.path.join(gold_dir, "gold_eval.tsv"), sep="\t", index=False)


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2], sys.argv[3:])
