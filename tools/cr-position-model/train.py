from __future__ import annotations

import argparse
import json
import math
import os
import random
import re
import statistics
import sys
import time
from collections import Counter, defaultdict
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Iterable, Sequence

import numpy as np
import onnx
import onnxruntime as ort
import torch
from PIL import Image, ImageEnhance, ImageFilter
from torch import Tensor, nn
from torch.utils.data import DataLoader, Dataset


CLASS_NAMES = ("Invalid", "Valid")
INVALID_INDEX = 0
VALID_INDEX = 1
INPUT_WIDTH = 160
INPUT_HEIGHT = 96
CROP = {"x": 0.18, "y": 0.18, "width": 0.68, "height": 0.82}
NORMALIZATION_MEAN = (0.5, 0.5, 0.5)
NORMALIZATION_STD = (0.5, 0.5, 0.5)
VALID_THRESHOLD = 0.90
INVALID_THRESHOLD = 0.20
SUPPORTED_EXTENSIONS = {".png", ".jpg", ".jpeg", ".bmp", ".webp"}


@dataclass(frozen=True)
class Sample:
    path: Path
    label: int
    group: str


class RepeatedAugmentedDataset(Dataset[tuple[Tensor, Tensor]]):
    def __init__(self, samples: Sequence[Sample], repeats: int) -> None:
        if not samples:
            raise ValueError("O conjunto de treino não pode estar vazio.")
        if repeats < 1:
            raise ValueError("repeats deve ser positivo.")

        self._samples = tuple(samples)
        self._repeats = repeats
        self._images = {
            sample.path: load_position_crop(sample.path).resize(
                (INPUT_WIDTH, INPUT_HEIGHT), Image.Resampling.BILINEAR
            )
            for sample in self._samples
        }

    def __len__(self) -> int:
        return len(self._samples) * self._repeats

    def __getitem__(self, index: int) -> tuple[Tensor, Tensor]:
        sample = self._samples[index % len(self._samples)]
        image = augment_image(self._images[sample.path].copy())
        tensor = image_to_tensor(image)
        return tensor, torch.tensor(sample.label, dtype=torch.long)


class ConvNormRelu(nn.Sequential):
    def __init__(
        self,
        input_channels: int,
        output_channels: int,
        kernel_size: int,
        stride: int = 1,
        groups: int = 1,
    ) -> None:
        padding = kernel_size // 2
        super().__init__(
            nn.Conv2d(
                input_channels,
                output_channels,
                kernel_size,
                stride=stride,
                padding=padding,
                groups=groups,
                bias=False,
            ),
            nn.BatchNorm2d(output_channels),
            nn.ReLU(inplace=False),
        )


class DepthwiseSeparableBlock(nn.Sequential):
    def __init__(self, input_channels: int, output_channels: int, stride: int) -> None:
        super().__init__(
            ConvNormRelu(
                input_channels,
                input_channels,
                kernel_size=3,
                stride=stride,
                groups=input_channels,
            ),
            ConvNormRelu(input_channels, output_channels, kernel_size=1),
        )


class CrPositionTinyCnn(nn.Module):
    """CNN pequeno que preserva uma grade espacial antes da classificação."""

    def __init__(self) -> None:
        super().__init__()
        self.features = nn.Sequential(
            ConvNormRelu(3, 12, kernel_size=5, stride=2),
            DepthwiseSeparableBlock(12, 20, stride=2),
            DepthwiseSeparableBlock(20, 32, stride=2),
            DepthwiseSeparableBlock(32, 48, stride=2),
            nn.Conv2d(48, 16, kernel_size=1, bias=True),
            nn.ReLU(inplace=False),
        )
        self.spatial_pool = nn.AdaptiveAvgPool2d((3, 5))
        self.classifier = nn.Sequential(
            nn.Flatten(),
            nn.Linear(16 * 3 * 5, 32),
            nn.ReLU(inplace=False),
            nn.Dropout(p=0.15),
            nn.Linear(32, len(CLASS_NAMES)),
        )

    def forward(self, image: Tensor) -> Tensor:
        features = self.features(image)
        return self.classifier(self.spatial_pool(features))


def parse_args() -> argparse.Namespace:
    repository_root = Path(__file__).resolve().parents[2]
    parser = argparse.ArgumentParser(
        description="Treina e exporta o classificador leve de posição do Farm de CR."
    )
    parser.add_argument(
        "--dataset",
        type=Path,
        default=repository_root / "ExemplosPosition" / "Dataset",
    )
    parser.add_argument(
        "--output",
        type=Path,
        default=repository_root / "Assets" / "Vision" / "cr-position.onnx",
    )
    parser.add_argument(
        "--metadata",
        type=Path,
        default=repository_root / "Assets" / "Vision" / "cr-position-model.json",
    )
    parser.add_argument("--epochs", type=int, default=60)
    parser.add_argument("--repeats", type=int, default=32)
    parser.add_argument("--batch-size", type=int, default=16)
    parser.add_argument("--seed", type=int, default=20260820)
    parser.add_argument("--threads", type=int, default=2)
    parser.add_argument("--false-positive-cost", type=float, default=3.0)
    return parser.parse_args()


def seed_everything(seed: int, threads: int) -> None:
    if threads < 1:
        raise ValueError("threads deve ser positivo.")

    os.environ["OMP_NUM_THREADS"] = str(threads)
    os.environ["MKL_NUM_THREADS"] = str(threads)
    random.seed(seed)
    np.random.seed(seed)
    torch.manual_seed(seed)
    torch.set_num_threads(threads)
    torch.set_num_interop_threads(1)
    torch.use_deterministic_algorithms(True)


def infer_group_id(path: Path) -> str:
    # Coletas automáticas devem usar <attempt-id>__<frame-id>.png. Assim, todos
    # os frames da mesma tentativa permanecem no mesmo lado do split.
    stem = path.stem
    if "__" in stem:
        return stem.split("__", maxsplit=1)[0]

    # Compatibilidade com a primeira coleta de desenvolvimento, que utilizou
    # <attempt-id>-frameNN antes da convenção explícita de agrupamento.
    return re.sub(r"-frame\d+$", "", stem, flags=re.IGNORECASE)


def discover_samples(dataset_root: Path) -> list[Sample]:
    samples: list[Sample] = []
    for label, class_name in enumerate(CLASS_NAMES):
        class_directory = dataset_root / class_name
        if not class_directory.is_dir():
            raise FileNotFoundError(f"Diretório ausente: {class_directory}")

        for path in sorted(class_directory.iterdir()):
            if path.is_file() and path.suffix.lower() in SUPPORTED_EXTENSIONS:
                samples.append(Sample(path.resolve(), label, infer_group_id(path)))

    if not samples:
        raise RuntimeError(f"Nenhuma imagem encontrada em {dataset_root}.")

    labels_by_group: dict[str, set[int]] = defaultdict(set)
    for sample in samples:
        labels_by_group[sample.group].add(sample.label)
    inconsistent = {
        group: labels
        for group, labels in labels_by_group.items()
        if len(labels) != 1
    }
    if inconsistent:
        raise RuntimeError(
            "Uma mesma tentativa não pode ter labels diferentes: "
            f"{inconsistent}"
        )

    return samples


def grouped_split(
    samples: Sequence[Sample], seed: int, validation_fraction: float = 0.20
) -> tuple[list[Sample], list[Sample], dict[str, Any]]:
    groups_by_label: dict[int, list[str]] = defaultdict(list)
    for label in range(len(CLASS_NAMES)):
        groups_by_label[label] = sorted(
            {sample.group for sample in samples if sample.label == label}
        )

    rng = random.Random(seed)
    validation_groups: set[str] = set()
    for label in range(len(CLASS_NAMES)):
        groups = groups_by_label[label]
        rng.shuffle(groups)
        if len(groups) < 2:
            continue
        requested = max(1, int(round(len(groups) * validation_fraction)))
        validation_count = min(requested, len(groups) - 1)
        validation_groups.update(groups[:validation_count])

    training = [sample for sample in samples if sample.group not in validation_groups]
    validation = [sample for sample in samples if sample.group in validation_groups]
    validation_labels = sorted({sample.label for sample in validation})
    coverage = {
        "strategy": "grouped-holdout-by-origin-or-attempt",
        "groupConvention": "<attempt-id>__<frame-id> or filename stem",
        "validationFractionRequested": validation_fraction,
        "trainingGroups": sorted({sample.group for sample in training}),
        "validationGroups": sorted(validation_groups),
        "validationClasses": [CLASS_NAMES[label] for label in validation_labels],
        "completeClassCoverage": len(validation_labels) == len(CLASS_NAMES),
        "canMeasureFalsePositives": INVALID_INDEX in validation_labels,
        "canMeasureFalseNegatives": VALID_INDEX in validation_labels,
    }
    return training, validation, coverage


def load_position_crop(path: Path) -> Image.Image:
    with Image.open(path) as source:
        image = source.convert("RGB")

    left = max(0, min(round(image.width * CROP["x"]), image.width - 1))
    top = max(0, min(round(image.height * CROP["y"]), image.height - 1))
    width = max(1, min(round(image.width * CROP["width"]), image.width - left))
    height = max(1, min(round(image.height * CROP["height"]), image.height - top))
    return image.crop((left, top, left + width, top + height))


def augment_image(image: Image.Image) -> Image.Image:
    image = ImageEnhance.Brightness(image).enhance(random.uniform(0.86, 1.14))
    image = ImageEnhance.Contrast(image).enhance(random.uniform(0.88, 1.12))
    image = ImageEnhance.Color(image).enhance(random.uniform(0.92, 1.08))

    if random.random() < 0.85:
        angle = random.uniform(-1.5, 1.5)
        translate = (
            round(image.width * random.uniform(-0.015, 0.015)),
            round(image.height * random.uniform(-0.015, 0.015)),
        )
        fill_color = tuple(
            int(value)
            for value in np.asarray(
                image.resize((1, 1), Image.Resampling.BILINEAR), dtype=np.uint8
            ).reshape(3)
        )
        image = image.rotate(
            angle,
            resample=Image.Resampling.BILINEAR,
            translate=translate,
            fillcolor=fill_color,
        )

    if random.random() < 0.15:
        image = image.filter(ImageFilter.GaussianBlur(radius=random.uniform(0.1, 0.6)))
    return image


def image_to_tensor(image: Image.Image) -> Tensor:
    resized = image.resize((INPUT_WIDTH, INPUT_HEIGHT), Image.Resampling.BILINEAR)
    pixels = np.asarray(resized, dtype=np.float32) / 255.0
    pixels = (pixels - np.asarray(NORMALIZATION_MEAN, dtype=np.float32)) / np.asarray(
        NORMALIZATION_STD, dtype=np.float32
    )
    channels_first = np.transpose(pixels, (2, 0, 1)).copy()
    return torch.from_numpy(channels_first)


def clean_tensor(sample: Sample) -> Tensor:
    return image_to_tensor(load_position_crop(sample.path))


def train_model(
    samples: Sequence[Sample],
    *,
    seed: int,
    epochs: int,
    repeats: int,
    batch_size: int,
    false_positive_cost: float,
) -> tuple[CrPositionTinyCnn, dict[str, Any]]:
    if {sample.label for sample in samples} != set(range(len(CLASS_NAMES))):
        raise RuntimeError(
            "O treino exige ao menos uma origem de cada classe. "
            f"Classes presentes: {sorted({sample.label for sample in samples})}"
        )

    seed_everything_for_model(seed)
    model = CrPositionTinyCnn()
    dataset = RepeatedAugmentedDataset(samples, repeats)
    generator = torch.Generator().manual_seed(seed)
    loader = DataLoader(
        dataset,
        batch_size=batch_size,
        shuffle=True,
        num_workers=0,
        drop_last=False,
        generator=generator,
    )
    class_weights = torch.tensor(
        [false_positive_cost, 1.0], dtype=torch.float32
    )
    criterion = nn.CrossEntropyLoss(weight=class_weights, label_smoothing=0.03)
    optimizer = torch.optim.AdamW(model.parameters(), lr=3e-3, weight_decay=1e-3)
    scheduler = torch.optim.lr_scheduler.CosineAnnealingLR(
        optimizer, T_max=max(1, epochs), eta_min=1e-5
    )

    epoch_losses: list[float] = []
    started = time.perf_counter()
    for epoch in range(epochs):
        model.train()
        running_loss = 0.0
        seen = 0
        for images, labels in loader:
            optimizer.zero_grad(set_to_none=True)
            logits = model(images)
            loss = criterion(logits, labels)
            loss.backward()
            nn.utils.clip_grad_norm_(model.parameters(), max_norm=5.0)
            optimizer.step()
            running_loss += float(loss.item()) * labels.numel()
            seen += labels.numel()
        scheduler.step()
        epoch_loss = running_loss / max(1, seen)
        epoch_losses.append(epoch_loss)
        if epoch == 0 or (epoch + 1) % 10 == 0 or epoch + 1 == epochs:
            print(
                f"epoch={epoch + 1:03d}/{epochs} "
                f"loss={epoch_loss:.6f} lr={scheduler.get_last_lr()[0]:.6g}"
            )

    model.eval()
    elapsed = time.perf_counter() - started
    return model, {
        "epochs": epochs,
        "repeatsPerOrigin": repeats,
        "batchSize": batch_size,
        "finalLoss": epoch_losses[-1],
        "minimumLoss": min(epoch_losses),
        "elapsedSeconds": elapsed,
    }


def seed_everything_for_model(seed: int) -> None:
    random.seed(seed)
    np.random.seed(seed)
    torch.manual_seed(seed)


@torch.inference_mode()
def predict_pytorch(
    model: nn.Module, samples: Sequence[Sample]
) -> list[dict[str, Any]]:
    predictions: list[dict[str, Any]] = []
    for sample in samples:
        input_tensor = clean_tensor(sample).unsqueeze(0)
        logits = model(input_tensor)[0]
        probabilities = torch.softmax(logits, dim=0)
        predictions.append(
            {
                "path": sample.path,
                "group": sample.group,
                "label": sample.label,
                "logits": logits.detach().cpu().numpy().astype(np.float32),
                "probabilities": probabilities.detach()
                .cpu()
                .numpy()
                .astype(np.float32),
            }
        )
    return predictions


def summarize_predictions(
    predictions: Sequence[dict[str, Any]], repository_root: Path
) -> dict[str, Any]:
    confusion = [[0, 0], [0, 0]]
    tri_state = Counter()
    false_positives = 0
    false_negatives = 0
    per_sample: list[dict[str, Any]] = []

    for prediction in predictions:
        label = int(prediction["label"])
        probabilities = prediction["probabilities"]
        predicted_index = int(np.argmax(probabilities))
        confusion[label][predicted_index] += 1
        valid_probability = float(probabilities[VALID_INDEX])
        if valid_probability >= VALID_THRESHOLD:
            decision = "Valid"
            false_positives += int(label == INVALID_INDEX)
        elif valid_probability <= INVALID_THRESHOLD:
            decision = "Invalid"
            false_negatives += int(label == VALID_INDEX)
        else:
            decision = "Unknown"
        tri_state[decision] += 1

        try:
            relative_path = prediction["path"].relative_to(repository_root).as_posix()
        except ValueError:
            relative_path = str(prediction["path"])
        per_sample.append(
            {
                "file": relative_path,
                "group": prediction["group"],
                "groundTruth": CLASS_NAMES[label],
                "probabilityInvalid": round(float(probabilities[INVALID_INDEX]), 8),
                "probabilityValid": round(valid_probability, 8),
                "argmax": CLASS_NAMES[predicted_index],
                "decision": decision,
            }
        )

    correct = confusion[0][0] + confusion[1][1]
    total = sum(sum(row) for row in confusion)
    return {
        "sampleCount": total,
        "argmaxAccuracy": correct / total if total else None,
        "confusionMatrix": {
            "rows": list(CLASS_NAMES),
            "columns": list(CLASS_NAMES),
            "values": confusion,
        },
        "decisionThresholds": {
            "validAtOrAbove": VALID_THRESHOLD,
            "invalidAtOrBelow": INVALID_THRESHOLD,
        },
        "decisionCounts": {
            "Invalid": tri_state["Invalid"],
            "Valid": tri_state["Valid"],
            "Unknown": tri_state["Unknown"],
        },
        "falsePositives": false_positives,
        "falseNegatives": false_negatives,
        "samples": per_sample,
    }


def export_onnx(model: nn.Module, output_path: Path) -> None:
    output_path.parent.mkdir(parents=True, exist_ok=True)
    dummy = torch.zeros(1, 3, INPUT_HEIGHT, INPUT_WIDTH, dtype=torch.float32)
    torch.onnx.export(
        model,
        (dummy,),
        output_path,
        input_names=["image"],
        output_names=["logits"],
        opset_version=18,
        dynamo=True,
        external_data=False,
        optimize=True,
        verbose=False,
    )
    checked = onnx.load(output_path)
    onnx.checker.check_model(checked, full_check=True)


def create_ort_session(model_path: Path) -> ort.InferenceSession:
    options = ort.SessionOptions()
    options.intra_op_num_threads = 1
    options.inter_op_num_threads = 1
    options.execution_mode = ort.ExecutionMode.ORT_SEQUENTIAL
    options.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_ALL
    options.add_session_config_entry("session.intra_op.allow_spinning", "0")
    options.add_session_config_entry("session.inter_op.allow_spinning", "0")
    return ort.InferenceSession(
        str(model_path),
        sess_options=options,
        providers=["CPUExecutionProvider"],
    )


def validate_onnx_equivalence(
    session: ort.InferenceSession,
    predictions: Sequence[dict[str, Any]],
) -> dict[str, Any]:
    maximum_absolute_error = 0.0
    maximum_relative_error = 0.0
    for prediction in predictions:
        sample = Sample(
            prediction["path"], int(prediction["label"]), prediction["group"]
        )
        input_array = clean_tensor(sample).unsqueeze(0).numpy()
        actual = session.run(["logits"], {"image": input_array})[0][0]
        expected = prediction["logits"]
        absolute = np.abs(actual - expected)
        relative = absolute / np.maximum(np.abs(expected), 1e-6)
        maximum_absolute_error = max(maximum_absolute_error, float(absolute.max()))
        maximum_relative_error = max(maximum_relative_error, float(relative.max()))
        np.testing.assert_allclose(actual, expected, rtol=1e-4, atol=1e-5)

    return {
        "samplesCompared": len(predictions),
        "rtol": 1e-4,
        "atol": 1e-5,
        "maximumAbsoluteError": maximum_absolute_error,
        "maximumRelativeError": maximum_relative_error,
        "passed": True,
    }


def benchmark_onnx(
    session: ort.InferenceSession, sample: Sample, warmup: int = 30, runs: int = 300
) -> dict[str, Any]:
    input_array = clean_tensor(sample).unsqueeze(0).numpy()
    feeds = {"image": input_array}
    for _ in range(warmup):
        session.run(["logits"], feeds)

    elapsed_ms: list[float] = []
    for _ in range(runs):
        started = time.perf_counter_ns()
        session.run(["logits"], feeds)
        elapsed_ms.append((time.perf_counter_ns() - started) / 1_000_000)

    ordered = sorted(elapsed_ms)
    p95_index = min(len(ordered) - 1, math.ceil(len(ordered) * 0.95) - 1)
    return {
        "scope": "ONNX inference only; capture and preprocessing excluded",
        "provider": session.get_providers()[0],
        "intraOpThreads": 1,
        "interOpThreads": 1,
        "executionMode": "sequential",
        "threadSpinning": False,
        "warmupRuns": warmup,
        "measuredRuns": runs,
        "medianMilliseconds": statistics.median(elapsed_ms),
        "p95Milliseconds": ordered[p95_index],
        "minimumMilliseconds": ordered[0],
        "maximumMilliseconds": ordered[-1],
    }


def class_and_group_counts(samples: Iterable[Sample]) -> dict[str, Any]:
    sample_counts = Counter(CLASS_NAMES[sample.label] for sample in samples)
    groups: dict[str, set[str]] = defaultdict(set)
    for sample in samples:
        groups[CLASS_NAMES[sample.label]].add(sample.group)
    return {
        "samples": {name: sample_counts[name] for name in CLASS_NAMES},
        "groups": {name: len(groups[name]) for name in CLASS_NAMES},
        "totalSamples": sum(sample_counts.values()),
        "totalGroups": len({group for values in groups.values() for group in values}),
    }


def write_metadata(path: Path, payload: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(
        json.dumps(payload, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )


def main() -> int:
    args = parse_args()
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    if hasattr(sys.stderr, "reconfigure"):
        sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    repository_root = Path(__file__).resolve().parents[2]
    seed_everything(args.seed, args.threads)

    samples = discover_samples(args.dataset.resolve())
    training_samples, validation_samples, split_coverage = grouped_split(
        samples, args.seed
    )
    counts = class_and_group_counts(samples)
    print(f"dataset={args.dataset.resolve()}")
    print(json.dumps(counts, ensure_ascii=False))
    print(json.dumps(split_coverage, ensure_ascii=False))

    holdout_metrics: dict[str, Any]
    if validation_samples:
        audit_model, audit_training = train_model(
            training_samples,
            seed=args.seed + 1,
            epochs=args.epochs,
            repeats=args.repeats,
            batch_size=args.batch_size,
            false_positive_cost=args.false_positive_cost,
        )
        holdout_metrics = {
            "available": True,
            "coverage": split_coverage,
            "training": audit_training,
            "results": summarize_predictions(
                predict_pytorch(audit_model, validation_samples), repository_root
            ),
            "warning": (
                "Holdout agrupado pequeno, com uma origem independente por classe. "
                "Mede esta amostra de validação, mas ainda não estima generalização."
                if split_coverage["completeClassCoverage"]
                else "Holdout agrupado parcial; falta cobertura independente de uma classe."
            ),
        }
    else:
        holdout_metrics = {
            "available": False,
            "coverage": split_coverage,
            "warning": (
                "Não há ao menos duas origens independentes por classe para formar "
                "um holdout agrupado sem remover uma classe inteira do treino."
            ),
        }

    final_model, final_training = train_model(
        samples,
        seed=args.seed,
        epochs=args.epochs,
        repeats=args.repeats,
        batch_size=args.batch_size,
        false_positive_cost=args.false_positive_cost,
    )
    final_predictions = predict_pytorch(final_model, samples)
    resubstitution = summarize_predictions(final_predictions, repository_root)
    resubstitution["warning"] = (
        "Métrica de ressubstituição nas mesmas origens usadas no treino; comprova "
        "somente ajuste/exportação e não mede generalização."
    )

    export_onnx(final_model, args.output.resolve())
    session = create_ort_session(args.output.resolve())
    equivalence = validate_onnx_equivalence(session, final_predictions)
    latency = benchmark_onnx(session, samples[0])
    parameters = sum(parameter.numel() for parameter in final_model.parameters())
    model_size = args.output.resolve().stat().st_size

    metadata = {
        "schemaVersion": 1,
        "task": "cr-position-validity",
        "model": {
            "file": args.output.resolve().name,
            "format": "ONNX",
            "opset": 18,
            "architecture": "custom-depthwise-spatial-cnn",
            "parameters": parameters,
            "sizeBytes": model_size,
        },
        "input": {
            "name": "image",
            "shape": [1, 3, INPUT_HEIGHT, INPUT_WIDTH],
            "layout": "NCHW",
            "dataType": "float32",
            "colorOrder": "RGB",
            "cropNormalized": CROP,
            "cropRounding": "nearest, then clamp to image bounds",
            "resize": {"width": INPUT_WIDTH, "height": INPUT_HEIGHT, "mode": "bilinear"},
            "scale": "uint8 / 255",
            "normalization": {
                "mean": list(NORMALIZATION_MEAN),
                "std": list(NORMALIZATION_STD),
            },
        },
        "output": {
            "name": "logits",
            "shape": [1, len(CLASS_NAMES)],
            "classes": list(CLASS_NAMES),
            "activation": "softmax outside the model",
            "validClassIndex": VALID_INDEX,
        },
        "decision": {
            "validAtOrAbove": VALID_THRESHOLD,
            "invalidAtOrBelow": INVALID_THRESHOLD,
            "betweenThresholds": "Unknown",
            "policy": "Somente Valid autoriza execução; Invalid ou Unknown interrompem o fluxo.",
            "thresholdStatus": "conservative-bootstrap; collect more independent groups",
        },
        "dataset": {
            "root": args.dataset.resolve().relative_to(repository_root).as_posix(),
            **counts,
            "split": split_coverage,
        },
        "training": {
            "seed": args.seed,
            "cpuThreads": args.threads,
            "optimizer": "AdamW",
            "learningRate": 0.003,
            "weightDecay": 0.001,
            "loss": "weighted cross entropy with label smoothing 0.03",
            "falsePositiveCost": args.false_positive_cost,
            "augmentation": {
                "brightness": [0.86, 1.14],
                "contrast": [0.88, 1.12],
                "saturation": [0.92, 1.08],
                "rotationDegrees": [-1.5, 1.5],
                "translationFraction": [-0.015, 0.015],
                "gaussianBlurProbability": 0.15,
                "forbidden": ["horizontal flip", "vertical flip", "MixUp", "CutMix"],
            },
            "finalModel": final_training,
        },
        "metrics": {
            "holdout": holdout_metrics,
            "resubstitution": resubstitution,
            "onnxEquivalence": equivalence,
            "latency": latency,
            "honestConclusion": (
                f"O modelo foi treinado e exportado de verdade com {counts['totalGroups']} "
                "origens agrupadas, ainda insuficientes para alegar generalização. "
                "Colete novas tentativas e mantenha todos os frames de cada tentativa "
                "no mesmo grupo."
            ),
        },
        "runtime": {
            "validatedExecutionProvider": session.get_providers()[0],
            "recommendedExecutionProvider": "CPUExecutionProvider",
            "recommendedThreads": 1,
            "sessionLifetime": "single persistent session",
            "inferenceCadence": "only at CR-position decision points, never continuous polling",
        },
    }
    write_metadata(args.metadata.resolve(), metadata)

    print(f"onnx={args.output.resolve()}")
    print(f"metadata={args.metadata.resolve()}")
    print(f"parameters={parameters}")
    print(f"model_size_bytes={model_size}")
    print(
        "latency_ms="
        f"median:{latency['medianMilliseconds']:.4f},"
        f"p95:{latency['p95Milliseconds']:.4f}"
    )
    print(json.dumps(resubstitution, ensure_ascii=False, default=str))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
