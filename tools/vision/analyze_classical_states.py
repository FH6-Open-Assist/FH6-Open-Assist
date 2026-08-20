#!/usr/bin/env python3
"""Reproduz com OpenCV os detectores clássicos usados nos checkpoints do app."""

from __future__ import annotations

import argparse
import json
import os
from dataclasses import asdict, dataclass
from pathlib import Path

import cv2
import numpy as np


REGIONS = {
    "left_menu": (0.12, 0.24, 0.27, 0.78),
    "right_menu": (0.72, 0.24, 0.87, 0.78),
    "left_event_icon": (0.13, 0.27, 0.25, 0.50),
    "right_event_icon": (0.74, 0.27, 0.85, 0.50),
    "dialog_header": (0.32, 0.42, 0.68, 0.51),
    "dialog_body": (0.32, 0.50, 0.68, 0.58),
    "confirmation_header": (0.32, 0.37, 0.68, 0.46),
    "confirmation_body": (0.32, 0.46, 0.68, 0.63),
}


@dataclass(frozen=True)
class Features:
    dialog_lime: float
    dialog_dark: float
    confirmation_lime: float
    confirmation_dark: float
    confirmation_white: float
    left_magenta: float
    right_orange: float
    left_white: float
    right_white: float
    left_icon_lime: float
    right_icon_lime: float


@dataclass(frozen=True)
class Result:
    name: str
    expected: str | None
    predicted: str
    path: str
    features: Features


def crop(image: np.ndarray, name: str) -> np.ndarray:
    height, width = image.shape[:2]
    left, top, right, bottom = REGIONS[name]
    # Igual ao C#: amostragem 2x2 depois das coordenadas arredondadas.
    return image[
        round(top * height) : round(bottom * height) : 2,
        round(left * width) : round(right * width) : 2,
    ]


def channels(region: np.ndarray) -> tuple[np.ndarray, np.ndarray, np.ndarray]:
    blue, green, red = [region[:, :, index].astype(np.int16) for index in range(3)]
    return red, green, blue


def ratio(image: np.ndarray, region: str, color: str) -> float:
    red, green, blue = channels(crop(image, region))
    masks = {
        "magenta": (red >= 180) & (green <= 130) & (blue >= 70) & (red >= blue),
        "orange": (red >= 190) & (green >= 60) & (green <= 180) & (blue <= 100) & (red >= green + 40),
        "white": (red >= 210) & (green >= 210) & (blue >= 210),
        "lime": (red >= 120) & (green >= 190) & (blue <= 90) & (green >= red),
        "dark": (red <= 85) & (green <= 85) & (blue <= 85),
    }
    return float(masks[color].mean())


def extract(image: np.ndarray) -> Features:
    return Features(
        dialog_lime=ratio(image, "dialog_header", "lime"),
        dialog_dark=ratio(image, "dialog_body", "dark"),
        confirmation_lime=ratio(image, "confirmation_header", "lime"),
        confirmation_dark=ratio(image, "confirmation_body", "dark"),
        confirmation_white=ratio(image, "confirmation_body", "white"),
        left_magenta=ratio(image, "left_menu", "magenta"),
        right_orange=ratio(image, "right_menu", "orange"),
        left_white=ratio(image, "left_menu", "white"),
        right_white=ratio(image, "right_menu", "white"),
        left_icon_lime=ratio(image, "left_event_icon", "lime"),
        right_icon_lime=ratio(image, "right_event_icon", "lime"),
    )


def classify(features: Features) -> str:
    if features.dialog_lime >= 0.45 and features.dialog_dark >= 0.70:
        return "ControllerDisconnected"
    if (
        features.confirmation_lime >= 0.55
        and features.confirmation_dark >= 0.50
        and features.confirmation_white >= 0.10
    ):
        return "ConfirmationDialog"
    if features.left_magenta >= 0.45 and features.right_orange >= 0.45:
        return "StreetMenu"
    if (
        features.left_white >= 0.55
        and features.right_white >= 0.55
        and features.left_icon_lime >= 0.035
        and features.right_icon_lime >= 0.035
    ):
        return "EventMenu"
    return "Unknown"


def analyze(path: Path, expected: str | None) -> Result:
    image = cv2.imread(str(path), cv2.IMREAD_COLOR)
    if image is None:
        raise ValueError(f"OpenCV não conseguiu ler {path}")
    features = extract(image)
    return Result(path.stem, expected, classify(features), str(path.resolve()), features)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--repo-root",
        type=Path,
        default=Path(__file__).resolve().parents[2],
    )
    parser.add_argument(
        "--diagnostics",
        type=Path,
        default=Path(os.environ.get("LOCALAPPDATA", "")) / "FH6 Open Assist" / "diagnostics",
    )
    parser.add_argument("--json-output", type=Path)
    args = parser.parse_args()

    references = [
        (args.repo_root / "ExemplosPosition" / "menu_rua.png", "StreetMenu"),
        (args.repo_root / "ExemplosPosition" / "menu_evento.png", "EventMenu"),
    ]
    diagnostics = []
    if args.diagnostics.exists():
        known_diagnostics = {
            "013237509": "ConfirmationDialog",
            "014914269": "ConfirmationDialog",
            "015856793": "ControllerDisconnected",
            "021621671": "StreetMenu",
            "022058473": "ConfirmationDialog",
        }
        for path in sorted(args.diagnostics.glob("*.png")):
            expected = next(
                (label for fragment, label in known_diagnostics.items() if fragment in path.stem),
                None,
            )
            diagnostics.append((path, expected))

    results = [analyze(path, expected) for path, expected in [*references, *diagnostics]]
    print("expected               predicted              dialog     street     event      arquivo")
    for result in results:
        feature = result.features
        print(
            f"{(result.expected or '-'):22} {result.predicted:22} "
            f"{feature.dialog_lime:5.1%}/{feature.dialog_dark:5.1%} "
            f"{feature.left_magenta:5.1%}/{feature.right_orange:5.1%} "
            f"{feature.left_white:5.1%}/{feature.right_white:5.1%} {result.name}"
        )

    labeled = [result for result in results if result.expected is not None]
    report = {
        "labeled": len(labeled),
        "correct": sum(result.expected == result.predicted for result in labeled),
        "false_positive_on_unlabeled_diagnostics": sum(
            result.predicted != "Unknown" for result in results if result.expected is None
        ),
        "warning": "Amostra pequena e dirigida; valida os layouts conhecidos, não estima generalização.",
        "results": [asdict(result) for result in results],
    }
    print(json.dumps({key: value for key, value in report.items() if key != "results"}, ensure_ascii=False, indent=2))
    if args.json_output:
        args.json_output.parent.mkdir(parents=True, exist_ok=True)
        args.json_output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        print(f"Relatório JSON: {args.json_output.resolve()}")

    return 0 if all(result.expected == result.predicted for result in labeled) else 1


if __name__ == "__main__":
    raise SystemExit(main())
