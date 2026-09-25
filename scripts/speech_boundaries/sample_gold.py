"""Sample line breaks from the test titles for hand labelling, with two lines of context on each side.

    python scripts/speech_boundaries/sample_gold.py path/to/breaks.tsv path/to/gold_unlabelled.jsonl 1000
"""
import json
import random
import sys

import pandas as pd

from train import split_of

sys.stdout.reconfigure(encoding="utf-8")

CONTEXT = 2


def main(tsv_path: str, out_path: str, count: int) -> None:
    df = pd.read_csv(tsv_path, sep="\t", quoting=3, keep_default_na=False, na_values=[],
                     usecols=["group", "file", "idx", "label", "cue_end", "prev", "next"],
                     dtype={"group": "string", "file": "string", "prev": "string", "next": "string"})
    df = df[df["group"].map(split_of) == "test"].reset_index(drop=True)

    rng = random.Random(20260924)
    picks = sorted(rng.sample(range(len(df)), count))
    with open(out_path, "w", encoding="utf-8") as f:
        for n, i in enumerate(picks):
            row = df.iloc[i]
            same_file = (df["group"] == row["group"]) & (df["file"] == row["file"])
            before = [df.at[j, "prev"] for j in range(i - CONTEXT, i) if j >= 0 and same_file.iat[j]]
            after = [df.at[j, "next"] for j in range(i + 1, i + 1 + CONTEXT) if j < len(df) and same_file.iat[j]]
            f.write(json.dumps({
                "id": n, "group": row["group"], "file": row["file"], "idx": int(row["idx"]),
                "auto_label": int(row["label"]), "cue_end": int(row["cue_end"]),
                "before": before, "prev": row["prev"], "next": row["next"], "after": after,
            }, ensure_ascii=False) + "\n")
    print(f"{count} breaks written to {out_path}")


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2], int(sys.argv[3]))
