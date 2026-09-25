#!/usr/bin/env python3
"""
Evaluation script for binary classification results.

Computes and reports the following metrics:
  - Overall accuracy and balanced accuracy (mean per-class recall)
  - Macro precision, recall, and F1
  - Per-band metrics (accuracy and balanced accuracy for each band)
  - Per-runtime accuracy
  - 2x2 confusion matrix
  - Expected calibration error (ECE) with 10 equal-width bins
  - Declared mass statistics (min, median, mean, 10th percentile)
  - Threshold sweep for the positive class (bug)
  - Timing statistics (mean and p95)

Input formats:
  --labels labels.json: JSON object mapping row id to label info
  --results results.jsonl: JSON lines with prediction results
  --json out.json: (optional) Dump metrics as JSON

Handles degenerate cases without crashing: empty inputs, bands with zero rows,
classes with zero true instances (reports n/a, avoiding a division by zero),
result rows that failed scoring (skipped, counted, and reported separately),
and abstentions where every declared option's letter was missing from the
model's output (excluded from accuracy-style metrics, counted separately).
"""

import argparse
import json
import math
import sys
from dataclasses import dataclass
from typing import Dict, List, Optional, Tuple


@dataclass
class LabelInfo:
    """Information about a single labeled example."""
    label: str  # "bug" or "external"
    band: str   # "clear_bug", "clear_external", "ambiguous"
    runtime: str


@dataclass
class PredictionResult:
    """Information about a single prediction."""
    id: str
    option_ids: List[str]
    probabilities: List[float]
    declared_mass: Optional[float] = None
    missing_options: Optional[List[str]] = None
    total_seconds: Optional[float] = None


def load_labels(path: str) -> Dict[str, LabelInfo]:
    """Load labels from JSON file."""
    with open(path) as f:
        data = json.load(f)
    result = {}
    for row_id, info in data.items():
        result[row_id] = LabelInfo(
            label=info["label"],
            band=info["band"],
            runtime=info["runtime"]
        )
    return result


def load_results(path: str) -> Tuple[List[PredictionResult], int]:
    """Load prediction results from JSONL file.

    Returns the parsed results together with a count of rows skipped for
    carrying an "error" key. The runner writes that key for rows that failed
    scoring, and a partial results file still scores correctly.
    """
    results = []
    skipped_errors = 0
    with open(path) as f:
        for line in f:
            data = json.loads(line)
            if "error" in data:
                skipped_errors += 1
                continue
            results.append(PredictionResult(
                id=data["id"],
                option_ids=data["option_ids"],
                probabilities=data["probabilities"],
                declared_mass=data.get("declared_mass"),
                missing_options=data.get("missing_options"),
                total_seconds=data.get("total_seconds")
            ))
    return results, skipped_errors


def get_predicted_label(result: PredictionResult) -> str:
    """Get the predicted label based on highest probability."""
    if not result.option_ids:
        return "unknown"
    max_idx = 0
    max_prob = result.probabilities[0]
    for i, prob in enumerate(result.probabilities):
        if prob > max_prob:
            max_prob = prob
            max_idx = i
    return result.option_ids[max_idx]


def is_abstention(result: PredictionResult) -> bool:
    """A result is an abstention when no declared option carried any probability mass.

    This happens when every option letter is missing from the model's top
    logprobs: probabilities are all zero, or missing_options covers every
    option id. Argmax over an all-zero vector would otherwise pick index 0
    and score it as a real prediction.
    """
    if result.probabilities and all(probability == 0.0 for probability in result.probabilities):
        return True
    if result.option_ids and result.missing_options is not None \
            and set(result.missing_options) >= set(result.option_ids):
        return True
    return False


def compute_mean(values: List[float]) -> float:
    """Compute mean of a list of values."""
    if not values:
        return 0.0
    return sum(values) / len(values)


def compute_median(values: List[float]) -> float:
    """Compute median of a list of values."""
    if not values:
        return 0.0
    sorted_vals = sorted(values)
    n = len(sorted_vals)
    if n % 2 == 1:
        return sorted_vals[n // 2]
    return (sorted_vals[n // 2 - 1] + sorted_vals[n // 2]) / 2


def compute_percentile(values: List[float], percentile: float) -> float:
    """Compute percentile of a list of values (0-100)."""
    if not values:
        return 0.0
    sorted_vals = sorted(values)
    idx = int((percentile / 100.0) * len(sorted_vals))
    idx = min(idx, len(sorted_vals) - 1)
    return sorted_vals[idx]


def compute_accuracy(true_labels: List[str], pred_labels: List[str]) -> float:
    """Compute accuracy."""
    if not true_labels:
        return 0.0
    correct = sum(1 for t, p in zip(true_labels, pred_labels) if t == p)
    return correct / len(true_labels)


def compute_per_class_recall(true_labels: List[str], pred_labels: List[str], label: str) -> float:
    """Compute recall for a specific class."""
    count = sum(1 for t in true_labels if t == label)
    if count == 0:
        return float('nan')
    correct = sum(1 for t, p in zip(true_labels, pred_labels) if t == label and p == label)
    return correct / count


def compute_per_class_precision(true_labels: List[str], pred_labels: List[str], label: str) -> float:
    """Compute precision for a specific class."""
    count = sum(1 for p in pred_labels if p == label)
    if count == 0:
        return float('nan')
    correct = sum(1 for t, p in zip(true_labels, pred_labels) if t == label and p == label)
    return correct / count


def compute_balanced_accuracy(true_labels: List[str], pred_labels: List[str]) -> float:
    """Compute balanced accuracy (mean per-class recall)."""
    classes = set(true_labels)
    recalls = []
    for cls in classes:
        r = compute_per_class_recall(true_labels, pred_labels, cls)
        if not (r != r):  # Check for NaN
            recalls.append(r)
    if not recalls:
        return 0.0
    return sum(recalls) / len(recalls)


def compute_confusion_matrix(true_labels: List[str], pred_labels: List[str]) -> Tuple[int, int, int, int]:
    """Compute 2x2 confusion matrix for binary classification (bug vs external).

    Returns: (TP, FP, FN, TN) where positive class is "bug"
    """
    tp = sum(1 for t, p in zip(true_labels, pred_labels) if t == "bug" and p == "bug")
    fp = sum(1 for t, p in zip(true_labels, pred_labels) if t == "external" and p == "bug")
    fn = sum(1 for t, p in zip(true_labels, pred_labels) if t == "bug" and p == "external")
    tn = sum(1 for t, p in zip(true_labels, pred_labels) if t == "external" and p == "external")
    return tp, fp, fn, tn


def compute_ece(true_labels: List[str], pred_probs: List[float], num_bins: int = 10) -> Tuple[float, List[Dict]]:
    """Compute expected calibration error with equal-width bins.

    Returns: (ECE, list of bin statistics)
    """
    if not true_labels:
        return 0.0, []

    bins = [[] for _ in range(num_bins)]
    bin_edges = [i / num_bins for i in range(num_bins + 1)]

    for true_label, pred_prob in zip(true_labels, pred_probs):
        bin_idx = int(pred_prob * num_bins)
        if bin_idx >= num_bins:
            bin_idx = num_bins - 1
        is_bug = 1 if true_label == "bug" else 0
        bins[bin_idx].append((pred_prob, is_bug))

    ece = 0.0
    bin_stats = []

    for i, bin_data in enumerate(bins):
        if bin_data:
            mean_conf = compute_mean([prob for prob, _ in bin_data])
            observed_bug_rate = compute_mean([is_bug for _, is_bug in bin_data])
            ece += len(bin_data) / len(true_labels) * abs(mean_conf - observed_bug_rate)
            bin_stats.append({
                "bin": i,
                "range": f"{bin_edges[i]:.2f}-{bin_edges[i+1]:.2f}",
                "count": len(bin_data),
                "mean_confidence": mean_conf,
                "observed_bug_rate": observed_bug_rate
            })

    return ece, bin_stats


def compute_threshold_sweep(true_labels: List[str], pred_probs: List[float]) -> List[Dict]:
    """Compute precision, recall, and forwarded count for different thresholds."""
    thresholds = [0.05 * (i + 1) for i in range(19)]  # 0.05, 0.10, ..., 0.95
    results = []

    for threshold in thresholds:
        pred_labels = ["bug" if prob >= threshold else "external" for prob in pred_probs]

        tp = sum(1 for t, p in zip(true_labels, pred_labels) if t == "bug" and p == "bug")
        fp = sum(1 for t, p in zip(true_labels, pred_labels) if t == "external" and p == "bug")
        fn = sum(1 for t, p in zip(true_labels, pred_labels) if t == "bug" and p == "external")

        # Precision is undefined when the threshold forwards nothing.
        precision = tp / (tp + fp) if (tp + fp) > 0 else float("nan")
        recall = tp / (tp + fn) if (tp + fn) > 0 else float("nan")
        forwarded = tp + fp

        results.append({
            "threshold": threshold,
            "precision": precision,
            "recall": recall,
            "forwarded": forwarded
        })

    return results


def find_highest_threshold_for_recall(sweep_results: List[Dict], target_recall: float = 0.95) -> Optional[float]:
    """Find the highest threshold that still achieves at least target_recall.

    Recall is monotonically non-increasing as the threshold rises, so the
    lowest qualifying threshold is always the lowest threshold in the sweep,
    which forwards everything and is useless as an operating point. The
    highest qualifying threshold filters the most rows while still meeting
    the recall target, so this scans from the top down and returns the first
    match. A NaN recall (no rows forwarded at that threshold) never
    qualifies, checked explicitly with math.isnan.
    """
    for result in reversed(sweep_results):
        recall = result["recall"]
        if math.isnan(recall):
            continue
        if recall >= target_recall:
            return result["threshold"]
    return None


def format_percentage(value: Optional[float]) -> str:
    """Format a value as a percentage, handling NaN."""
    if value is None or (value != value):  # NaN check
        return "n/a"
    return f"{value * 100:.1f}%"


def format_float(value: Optional[float], decimals: int = 4) -> str:
    """Format a float value, handling NaN."""
    if value is None or (value != value):  # NaN check
        return "n/a"
    return f"{value:.{decimals}f}"


def print_table(header: List[str], rows: List[List[str]], col_widths: Optional[List[int]] = None):
    """Print a fixed-width table using f-strings."""
    if not col_widths:
        col_widths = [max(len(h), max((len(r[i]) if i < len(r) else 0) for r in rows)) + 2 for i, h in enumerate(header)]

    header_str = " ".join(f"{h:<{col_widths[i]}}" for i, h in enumerate(header))
    print(header_str)
    print("-" * len(header_str))

    for row in rows:
        row_str = " ".join(f"{row[i] if i < len(row) else '':<{col_widths[i]}}" for i in range(len(header)))
        print(row_str)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--labels", required=True, help="Path to labels.json")
    parser.add_argument("--results", required=True, help="Path to results.jsonl")
    parser.add_argument("--json", help="Path to dump metrics as JSON")
    args = parser.parse_args()

    labels = load_labels(args.labels)
    results, skipped_error_rows = load_results(args.results)

    # Match results to labels
    result_map = {r.id: r for r in results}
    label_map = {rid: label for rid, label in labels.items()}

    # Report mismatches
    results_only = set(result_map.keys()) - set(label_map.keys())
    labels_only = set(label_map.keys()) - set(result_map.keys())
    if results_only:
        print(f"WARNING: {len(results_only)} row(s) in results but not in labels", file=sys.stderr)
    if labels_only:
        print(f"WARNING: {len(labels_only)} row(s) in labels but not in results", file=sys.stderr)

    # Keep only matched rows
    matched_ids = set(result_map.keys()) & set(label_map.keys())
    if not matched_ids:
        print("ERROR: No matched rows between labels and results")
        sys.exit(1)

    sorted_matched_ids = sorted(matched_ids)
    all_matched_results = [result_map[rid] for rid in sorted_matched_ids]

    # Abstentions (no declared option letter recovered) are excluded from every
    # accuracy-style metric below; scoring them would argmax an all-zero
    # probability vector into index 0, a meaningless prediction.
    abstained_ids = {rid for rid in sorted_matched_ids if is_abstention(result_map[rid])}
    abstained_count = len(abstained_ids)
    scored_ids = [rid for rid in sorted_matched_ids if rid not in abstained_ids]

    true_labels = [label_map[rid].label for rid in scored_ids]
    pred_results = [result_map[rid] for rid in scored_ids]
    pred_labels = [get_predicted_label(r) for r in pred_results]

    missing_options_count = sum(1 for r in pred_results if r.missing_options)

    # Get predicted probabilities for "bug" class
    pred_probs_bug = []
    for result in pred_results:
        try:
            bug_idx = result.option_ids.index("bug")
            pred_probs_bug.append(result.probabilities[bug_idx])
        except (ValueError, IndexError):
            pred_probs_bug.append(0.0)

    # Overall metrics
    overall_accuracy = compute_accuracy(true_labels, pred_labels)
    overall_balanced_accuracy = compute_balanced_accuracy(true_labels, pred_labels)

    # Macro precision, recall, F1
    bug_precision = compute_per_class_precision(true_labels, pred_labels, "bug")
    external_precision = compute_per_class_precision(true_labels, pred_labels, "external")
    bug_recall = compute_per_class_recall(true_labels, pred_labels, "bug")
    external_recall = compute_per_class_recall(true_labels, pred_labels, "external")

    # Average only the classes with a defined (finite) value. A class with no
    # true instances is excluded, so it does not drag the average toward
    # zero. Reports n/a (NaN) when neither class has one.
    finite_precisions = [p for p in (bug_precision, external_precision) if not math.isnan(p)]
    macro_precision = compute_mean(finite_precisions) if finite_precisions else float("nan")
    finite_recalls = [r for r in (bug_recall, external_recall) if not math.isnan(r)]
    macro_recall = compute_mean(finite_recalls) if finite_recalls else float("nan")
    macro_f1 = 2 * (macro_precision * macro_recall) / (macro_precision + macro_recall) if (macro_precision + macro_recall) > 0 else 0.0

    # Per-band metrics (scored rows only; abstentions are excluded above)
    bands_data = {}
    for rid, pred_label in zip(scored_ids, pred_labels):
        band = label_map[rid].band
        if band not in bands_data:
            bands_data[band] = {"true": [], "pred": []}
        bands_data[band]["true"].append(label_map[rid].label)
        bands_data[band]["pred"].append(pred_label)

    band_metrics = {}
    for band in ["clear_bug", "clear_external", "ambiguous", "held_out"]:
        if band in bands_data:
            data = bands_data[band]
            acc = compute_accuracy(data["true"], data["pred"])
            bal_acc = compute_balanced_accuracy(data["true"], data["pred"])
            band_metrics[band] = {"accuracy": acc, "balanced_accuracy": bal_acc, "count": len(data["true"])}
        else:
            # No scored rows in this band: reports n/a. A 0.0% would
            # misleadingly imply the band was scored.
            band_metrics[band] = {"accuracy": None, "balanced_accuracy": None, "count": 0}

    # Per-runtime metrics (scored rows only; abstentions are excluded above)
    runtime_data = {}
    for rid, pred_label in zip(scored_ids, pred_labels):
        runtime = label_map[rid].runtime
        if runtime not in runtime_data:
            runtime_data[runtime] = {"true": [], "pred": []}
        runtime_data[runtime]["true"].append(label_map[rid].label)
        runtime_data[runtime]["pred"].append(pred_label)

    runtime_metrics = {}
    for runtime, data in runtime_data.items():
        acc = compute_accuracy(data["true"], data["pred"])
        runtime_metrics[runtime] = {"accuracy": acc, "count": len(data["true"])}

    # Confusion matrix
    tp, fp, fn, tn = compute_confusion_matrix(true_labels, pred_labels)

    # ECE
    ece, bin_stats = compute_ece(true_labels, pred_probs_bug)

    # Declared mass (all matched rows, including abstentions: a low or zero
    # declared mass is the signal that a row abstained)
    declared_masses = [r.declared_mass for r in all_matched_results if r.declared_mass is not None]
    declared_mass_stats = None
    if declared_masses:
        declared_mass_stats = {
            "min": min(declared_masses),
            "median": compute_median(declared_masses),
            "mean": compute_mean(declared_masses),
            "p10": compute_percentile(declared_masses, 10)
        }

    # Threshold sweep (scored rows only)
    sweep_results = compute_threshold_sweep(true_labels, pred_probs_bug)
    threshold_for_95_recall = find_highest_threshold_for_recall(sweep_results, 0.95)

    # Timing (all matched rows, including abstentions: they still cost wall time)
    timing_stats = None
    total_seconds = [r.total_seconds for r in all_matched_results if r.total_seconds is not None]
    if total_seconds:
        p95_seconds = compute_percentile(total_seconds, 95)
        timing_stats = {
            "mean": compute_mean(total_seconds),
            "p95": p95_seconds
        }

    # Print report
    print("\n" + "=" * 60)
    print("OVERALL")
    print("=" * 60)
    print(f"Row count:          {len(matched_ids)}")
    print(f"Skipped {skipped_error_rows} row(s) that failed scoring")
    print(f"Abstained (no option letter recovered): {abstained_count}")
    print(f"Scored rows:        {len(scored_ids)}")
    print(f"Rows with missing option letters: {missing_options_count}")
    print(f"Accuracy:           {format_percentage(overall_accuracy)}")
    print(f"Balanced accuracy:  {format_percentage(overall_balanced_accuracy)}")
    print(f"Macro precision:    {format_percentage(macro_precision)}")
    print(f"Macro recall:       {format_percentage(macro_recall)}")
    print(f"Macro F1:           {format_percentage(macro_f1)}")
    if len(scored_ids) < 100:
        print("\nWARNING: fewer than 100 rows scored; the 10-bin ECE and the 19-step "
              "threshold sweep below are not meaningful at this sample size.")

    print("\n" + "=" * 60)
    print("PER BAND")
    print("=" * 60)
    band_order = ["clear_bug", "clear_external", "ambiguous", "held_out"]
    for band in band_order:
        if band in band_metrics:
            m = band_metrics[band]
            print(f"\n{band} (n={m['count']})")
            print(f"  Accuracy:          {format_percentage(m['accuracy'])}")
            print(f"  Balanced accuracy: {format_percentage(m['balanced_accuracy'])}")

    print("\n" + "=" * 60)
    print("PER RUNTIME")
    print("=" * 60)
    runtime_sorted = sorted(runtime_metrics.keys())
    table_data = []
    for runtime in runtime_sorted:
        m = runtime_metrics[runtime]
        table_data.append([runtime, str(m["count"]), format_percentage(m["accuracy"])])
    print_table(["Runtime", "Count", "Accuracy"], table_data)

    print("\n" + "=" * 60)
    print("CONFUSION MATRIX")
    print("=" * 60)
    print(f"                Predicted: bug  Predicted: external  ")
    print(f"Actual: bug              {tp:4d}              {fn:4d}")
    print(f"Actual: external         {fp:4d}              {tn:4d}")

    print("\n" + "=" * 60)
    print("CALIBRATION")
    print("=" * 60)
    print(f"Expected calibration error (ECE): {format_float(ece)}")
    print("\nBins hold rows by predicted P(bug). A calibrated model matches mean")
    print("confidence to the observed bug rate within each bin.")
    print_table(
        ["P(bug) bin", "Count", "Mean P(bug)", "Observed bug rate"],
        [[s["range"], str(s["count"]), format_float(s["mean_confidence"]), format_percentage(s["observed_bug_rate"])] for s in bin_stats]
    )

    if declared_mass_stats:
        print("\n" + "=" * 60)
        print("DECLARED MASS")
        print("=" * 60)
        print(f"Min:           {format_float(declared_mass_stats['min'])}")
        print(f"Median:        {format_float(declared_mass_stats['median'])}")
        print(f"Mean:          {format_float(declared_mass_stats['mean'])}")
        print(f"10th pctl:     {format_float(declared_mass_stats['p10'])}")
        if declared_mass_stats['median'] < 0.5:
            print("\nWARNING: median declared mass below 0.5 means the model put most of its probability on tokens other than the declared options; the renormalised probabilities may be close to noise.")

    print("\n" + "=" * 60)
    print("THRESHOLD SWEEP (positive class: bug)")
    print("=" * 60)
    table_data = []
    for result in sweep_results:
        table_data.append([
            format_float(result["threshold"], 2),
            format_percentage(result["precision"]),
            format_percentage(result["recall"]),
            str(result["forwarded"])
        ])
    print_table(["Threshold", "Precision", "Recall", "Forwarded"], table_data)

    if threshold_for_95_recall:
        print(f"\nHighest threshold still achieving recall >= 0.95: {format_float(threshold_for_95_recall, 2)}")
    else:
        print("\nNo threshold achieves recall >= 0.95")

    if timing_stats:
        print("\n" + "=" * 60)
        print("TIMING")
        print("=" * 60)
        print(f"Mean:  {format_float(timing_stats['mean'], 2)} seconds")
        print(f"P95:   {format_float(timing_stats['p95'], 2)} seconds")

    # Dump JSON if requested
    if args.json:
        metrics = {
            "overall": {
                "row_count": len(matched_ids),
                "skipped_error_rows": skipped_error_rows,
                "abstained_count": abstained_count,
                "scored_row_count": len(scored_ids),
                "missing_options_count": missing_options_count,
                "accuracy": overall_accuracy,
                "balanced_accuracy": overall_balanced_accuracy,
                "macro_precision": macro_precision,
                "macro_recall": macro_recall,
                "macro_f1": macro_f1
            },
            "per_band": band_metrics,
            "per_runtime": runtime_metrics,
            "confusion_matrix": {
                "tp": tp,
                "fp": fp,
                "fn": fn,
                "tn": tn
            },
            "calibration": {
                "ece": ece,
                "bins": bin_stats
            },
            "threshold_sweep": sweep_results,
            "threshold_for_95_recall": threshold_for_95_recall
        }
        if declared_mass_stats:
            metrics["declared_mass"] = declared_mass_stats
        if timing_stats:
            metrics["timing"] = timing_stats

        with open(args.json, "w") as f:
            json.dump(metrics, f, indent=2)


if __name__ == "__main__":
    main()
