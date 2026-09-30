"""Score the automatic labels, the Phase 2 rule and the model against the hand-labelled gold set.

    python scripts/speech_boundaries/eval_gold.py path/to/breaks.tsv path/to/model.json path/to/SpeechBoundaries
"""
import glob
import json
import os
import sys

import lightgbm as lgb
import numpy as np
import pandas as pd

from train import encode, load, report, rule_predict

sys.stdout.reconfigure(encoding="utf-8")


def main(tsv_path: str, model_path: str, gold_dir: str) -> None:
    unlabelled = {r["id"]: r for r in map(json.loads, open(os.path.join(gold_dir, "gold_unlabelled.jsonl"), encoding="utf-8"))}
    gold = {}
    for path in sorted(glob.glob(os.path.join(gold_dir, "gold_labels_*.jsonl"))):
        for line in open(path, encoding="utf-8"):
            if line.strip():
                r = json.loads(line)
                gold[r["id"]] = r
    missing = set(unlabelled) - set(gold)
    print(f"{len(gold)} gold labels, {len(missing)} missing")

    df = load(tsv_path)
    test = df[df["split"] == "test"].reset_index(drop=True)
    test["rule"] = rule_predict(test)

    with open(model_path, encoding="utf-8") as f:
        vocab = json.load(f)["vocab"]
    booster = lgb.Booster(model_file=model_path.replace(".json", ".lgb.txt"))
    test["prob"] = booster.predict(encode(test, vocab))

    # A gold break is only comparable while cleaning keeps both of its lines.
    key = test.set_index(["group", "file", "idx", "next_idx"])
    rows = []
    for i, g in gold.items():
        u = unlabelled[i]
        k = (u["group"], u["file"], u["idx"], u["idx"] + 1)
        if k not in key.index:
            continue
        t = key.loc[k]
        rows.append({"id": i, "gold": g["label"], "sure": g["sure"], "auto": int(t["label"]),
                     "rule": int(t["rule"]), "prob": float(t["prob"]), "prev": u["prev"], "next": u["next"]})
    ev = pd.DataFrame(rows)
    print(f"{len(ev)} gold breaks still present after cleaning")

    for name, subset in (("all", ev), ("sure only", ev[ev["sure"]])):
        y = subset["gold"].to_numpy()
        print(f"\n{name}: {len(subset)} breaks, {y.mean():.1%} boundaries")
        report("auto labels", y, subset["auto"].to_numpy())
        report("every break", y, np.ones_like(y))
        report("rule", y, subset["rule"].to_numpy())
        for threshold in (0.3, 0.4, 0.5, 0.6, 0.7):
            report(f"model @{threshold}", y, (subset["prob"] >= threshold).astype(int).to_numpy(), subset["prob"].to_numpy())

    wrong = ev[(ev["prob"] >= 0.5).astype(int) != ev["gold"]].sort_values("prob")
    wrong.to_csv(os.path.join(gold_dir, "gold_model_errors.tsv"), sep="\t", index=False)
    print(f"\n{len(wrong)} model errors written to gold_model_errors.tsv")


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2], sys.argv[3])
