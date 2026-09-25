"""Train the speech sentence-boundary model on the TSV written by `--export-speech-boundaries`.

Splits by title folder so no show appears on both sides, scores the Phase 2 rule on the same
test titles, and writes a JSON model (tree dump + category vocabularies) for the C# tree walker.

    python scripts/speech_boundaries/train.py path/to/breaks.tsv path/to/model.json [relabel_tau]
"""
import hashlib
import json
import sys
import time

import lightgbm as lgb
import numpy as np
import pandas as pd
from sklearn.metrics import log_loss, roc_auc_score

sys.stdout.reconfigure(encoding="utf-8")

CATEGORICAL = [
    "p1_pos", "p1_ctype", "p1_cform", "p1_surf",
    "p2_pos", "p2_cform", "p2_surf",
    "n1_pos", "n1_cform", "n1_surf",
    "n2_pos", "n2_surf",
    "p_first_char", "p_last_char", "n_first_char", "n_last_char", "p_last_char_before_arrow",
]
NUMERIC = ["p_len", "n_len", "p_tokens", "n_tokens", "p_bracket_depth", "n_bracket_depth",
           "doc_terminal_share", "doc_comma_share", "doc_mean_len"]
FEATURES = CATEGORICAL + NUMERIC

# Categories seen fewer times than this map to "unknown", so the vocabulary stays small and rare surfaces don't overfit.
MIN_CATEGORY_COUNT = 200
MAX_JOINED_LINES = 3
PARITY_ROWS = 20_000
CONTINUATION_CHARS = {"、", "，", "➡", "→", "―", "—", "・"}


def split_of(group: str) -> str:
    bucket = int(hashlib.md5(group.encode("utf-8")).hexdigest(), 16) % 10
    return "test" if bucket == 0 else "valid" if bucket == 1 else "train"


def load(path: str) -> pd.DataFrame:
    df = pd.read_csv(
        path, sep="\t", quoting=3, keep_default_na=False, na_values=[],
        usecols=["group", "file", "idx", "next_idx", "label", "cue_end"] + FEATURES,
        dtype={c: "string" for c in CATEGORICAL + ["group", "file"]},
    )
    df["split"] = df["group"].map(split_of)
    return df


def build_vocab(train: pd.DataFrame) -> dict[str, list[str]]:
    vocab = {}
    for col in CATEGORICAL:
        counts = train[col].value_counts()
        vocab[col] = sorted(counts[counts >= MIN_CATEGORY_COUNT].index.tolist())
    return vocab


def encode(df: pd.DataFrame, vocab: dict[str, list[str]]) -> pd.DataFrame:
    x = pd.DataFrame(index=df.index)
    for col in CATEGORICAL:
        index = {v: i for i, v in enumerate(vocab[col])}
        x[col] = df[col].map(index).fillna(-1).astype("int32")
    for col in NUMERIC:
        x[col] = df[col].astype("float32")
    return x


def rule_predict(df: pd.DataFrame) -> np.ndarray:
    """Phase 2 rule: every break ends a sentence unless the line cannot end one; at most MAX_JOINED_LINES lines join."""
    cannot_end = (
        df["p_last_char"].isin(CONTINUATION_CHARS)
        | (df["p1_pos"].str.startswith("助詞-") & (df["p1_pos"] != "助詞-終助詞"))
    ).to_numpy()
    files = (df["group"] + "/" + df["file"]).to_numpy()
    pred = np.ones(len(df), dtype=np.int8)
    joined = 1
    for i in range(len(df)):
        if i > 0 and files[i] != files[i - 1]:
            joined = 1
        if cannot_end[i] and joined < MAX_JOINED_LINES:
            pred[i] = 0
            joined += 1
        else:
            joined = 1
    return pred


def report(name: str, y: np.ndarray, pred: np.ndarray, prob: np.ndarray | None = None) -> None:
    acc = (pred == y).mean()
    tp = ((pred == 1) & (y == 1)).sum()
    fp = ((pred == 1) & (y == 0)).sum()
    fn = ((pred == 0) & (y == 1)).sum()
    tn = ((pred == 0) & (y == 0)).sum()
    line = (f"{name:<14} acc {acc:.4f}  boundary P {tp / max(tp + fp, 1):.4f} R {tp / max(tp + fn, 1):.4f}"
            f"  join P {tn / max(tn + fn, 1):.4f} R {tn / max(tn + fp, 1):.4f}")
    if prob is not None:
        line += f"  AUC {roc_auc_score(y, prob):.4f}  logloss {log_loss(y, prob):.4f}"
    print(line)


# The only node fields SpeechBoundaryModel reads; LightGBM's per-node statistics would triple the file.
NODE_FIELDS = {"split_feature", "threshold", "decision_type", "default_left", "missing_type", "left_child", "right_child", "leaf_value"}


def prune_tree(node: dict) -> dict:
    return {k: (prune_tree(v) if k in ("left_child", "right_child") else v) for k, v in node.items() if k in NODE_FIELDS}


def main(tsv_path: str, model_path: str) -> None:
    t0 = time.time()
    df = load(tsv_path)
    print(f"{len(df):,} breaks from {df['group'].nunique():,} titles, {df['label'].mean():.1%} boundaries "
          f"({time.time() - t0:.0f}s to load)")
    print(df.groupby("split").agg(rows=("label", "size"), titles=("group", "nunique"), boundary=("label", "mean")))

    train, valid, test = (df[df["split"] == s] for s in ("train", "valid", "test"))
    vocab = build_vocab(train)
    print("vocab sizes:", {k: len(v) for k, v in vocab.items()})

    x_train, x_valid, x_test = encode(train, vocab), encode(valid, vocab), encode(test, vocab)
    params = {
        "objective": "binary",
        "learning_rate": 0.2,
        "num_leaves": 15,
        "min_data_in_leaf": 200,
        "min_data_per_group": 200,
        "cat_smooth": 20,
        "max_cat_to_onehot": 8,
        "feature_fraction": 0.9,
        "bagging_fraction": 0.8,
        "bagging_freq": 1,
        "verbose": -1,
        "num_threads": 0,
    }
    dtrain = lgb.Dataset(x_train, train["label"], categorical_feature=CATEGORICAL, free_raw_data=False)
    dvalid = lgb.Dataset(x_valid, valid["label"], categorical_feature=CATEGORICAL, reference=dtrain)
    t0 = time.time()
    booster = lgb.train(params, dtrain, num_boost_round=1000, valid_sets=[dvalid],
                        callbacks=[lgb.early_stopping(30), lgb.log_evaluation(50)])
    print(f"trained {booster.best_iteration} trees in {time.time() - t0:.0f}s")

    y = test["label"].to_numpy()
    prob = booster.predict(x_test, num_iteration=booster.best_iteration)
    print("\nTest titles:")
    report("every break", y, np.ones_like(y))
    report("rule", y, rule_predict(test))
    for threshold in (0.3, 0.4, 0.5, 0.6, 0.7):
        report(f"model @{threshold}", y, (prob >= threshold).astype(np.int8), prob)

    importance = sorted(zip(FEATURES, booster.feature_importance("gain")), key=lambda p: -p[1])
    print("\ngain:", ", ".join(f"{n} {g:.0f}" for n, g in importance))

    model = {
        "features": FEATURES,
        "categorical": CATEGORICAL,
        "vocab": vocab,
        "best_iteration": booster.best_iteration,
        "trees": [{"tree_structure": prune_tree(t["tree_structure"])}
                  for t in booster.dump_model(num_iteration=booster.best_iteration)["tree_info"]],
    }
    with open(model_path, "w", encoding="utf-8") as f:
        json.dump(model, f, ensure_ascii=False, separators=(",", ":"))
    booster.save_model(model_path.replace(".json", ".lgb.txt"), num_iteration=booster.best_iteration)
    test.assign(prob=prob)[["group", "file", "idx", "label", "prob"]].to_csv(
        model_path.replace(".json", ".test_predictions.tsv"), sep="\t", index=False)
    test.assign(prob=prob).head(PARITY_ROWS)[FEATURES + ["prob"]].to_csv(
        model_path.replace(".json", ".parity.tsv"), sep="\t", index=False, quoting=3, float_format="%.9g")
    print(f"\nModel written to {model_path}")


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
