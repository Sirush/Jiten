"""Score models per channel on held-out YouTube rows (punctuation stripped, labelled by it).

    python scripts/speech_boundaries/eval_youtube.py path/to/youtube_breaks.tsv path/to/holdout.txt model_a.json [model_b.json ...]
"""
import json
import sys

import lightgbm as lgb
import numpy as np
import pandas as pd

from train import encode, load, report, rule_predict

sys.stdout.reconfigure(encoding="utf-8")


def predict(df: pd.DataFrame, model_path: str) -> np.ndarray:
    with open(model_path, encoding="utf-8") as f:
        vocab = json.load(f)["vocab"]
    booster = lgb.Booster(model_file=model_path.replace(".json", ".lgb.txt"))
    return booster.predict(encode(df, vocab))


def main(tsv_path: str, holdout_path: str, model_paths: list[str]) -> None:
    with open(holdout_path, encoding="utf-8") as f:
        holdout = {line.strip() for line in f if line.strip()}
    df = load(tsv_path)
    df = df[df["group"].isin(holdout)].reset_index(drop=True)
    df["rule"] = rule_predict(df)
    probs = {path: predict(df, path) for path in model_paths}

    y = df["label"].to_numpy()
    print(f"{len(df):,} held-out breaks from {df['group'].nunique()} channels, {y.mean():.1%} boundaries\n")
    report("every break", y, np.ones_like(y))
    report("rule", y, df["rule"].to_numpy())
    for path, prob in probs.items():
        report(path.split("/")[-1][:14], y, (prob >= 0.5).astype(np.int8), prob)

    names = [path.split("/")[-1].replace(".json", "") for path in model_paths]
    print(f"\n{'rows':>7} {'bound':>6} {'every':>6} {'rule':>6} " + " ".join(f"{n[:12]:>12}" for n in names) + "  channel")
    for group, idx in df.groupby("group").groups.items():
        yg = y[idx]
        accs = [((probs[p][idx] >= 0.5).astype(int) == yg).mean() for p in model_paths]
        print(f"{len(idx):>7} {yg.mean():>6.1%} {yg.mean():>6.1%} {(df['rule'].to_numpy()[idx] == yg).mean():>6.1%} "
              + " ".join(f"{a:>12.1%}" for a in accs) + f"  {group.removeprefix('youtube/')}")


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2], sys.argv[3:])
