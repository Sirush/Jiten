"""Sample held-out YouTube line breaks for hand labelling, capped per channel so the largest channels don't dominate.

    python scripts/speech_boundaries/sample_gold_youtube.py youtube_breaks.tsv holdout.txt out_dir 40 5
"""
import json
import os
import random
import sys

import pandas as pd

sys.stdout.reconfigure(encoding="utf-8")

CONTEXT = 2


def main(tsv_path: str, holdout_path: str, out_dir: str, per_channel: int, chunks: int) -> None:
    with open(holdout_path, encoding="utf-8") as f:
        holdout = {line.strip() for line in f if line.strip()}
    df = pd.read_csv(tsv_path, sep="\t", quoting=3, keep_default_na=False, na_values=[],
                     usecols=["group", "file", "idx", "next_idx", "label", "prev", "next"],
                     dtype={"group": "string", "file": "string", "prev": "string", "next": "string"})
    df = df[df["group"].isin(holdout)].reset_index(drop=True)

    rng = random.Random(20260928)
    picks = []
    for _, idx in sorted(df.groupby("group").groups.items()):
        picks += rng.sample(list(idx), min(per_channel, len(idx)))
    rng.shuffle(picks)

    os.makedirs(out_dir, exist_ok=True)
    unlabelled = []
    for n, i in enumerate(picks):
        row = df.iloc[i]
        same_file = lambda j: 0 <= j < len(df) and df.at[j, "group"] == row["group"] and df.at[j, "file"] == row["file"]
        before = [df.at[j, "prev"] for j in range(i - CONTEXT, i) if same_file(j)]
        after = [df.at[j, "next"] for j in range(i + 1, i + 1 + CONTEXT) if same_file(j)]
        unlabelled.append({"id": n, "group": row["group"], "file": row["file"], "idx": int(row["idx"]),
                           "next_idx": int(row["next_idx"]), "auto_label": int(row["label"]),
                           "before": before, "prev": row["prev"], "next": row["next"], "after": after})

    with open(os.path.join(out_dir, "gold_unlabelled.jsonl"), "w", encoding="utf-8") as f:
        for r in unlabelled:
            f.write(json.dumps(r, ensure_ascii=False) + "\n")

    size = -(-len(unlabelled) // chunks)
    for c in range(chunks):
        with open(os.path.join(out_dir, f"gold_chunk_{c}.jsonl"), "w", encoding="utf-8") as f:
            for r in unlabelled[c * size:(c + 1) * size]:
                f.write(json.dumps({k: r[k] for k in ("id", "before", "prev", "next", "after")}, ensure_ascii=False) + "\n")
    print(f"{len(unlabelled)} breaks from {df['group'].nunique()} channels in {chunks} chunks of up to {size}")


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2], sys.argv[3], int(sys.argv[4]), int(sys.argv[5]))
