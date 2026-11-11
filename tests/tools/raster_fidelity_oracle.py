"""Independent Poppler/scikit-image raster-fidelity oracle for synthetic fixtures."""

from __future__ import annotations

import argparse
import json
import platform
import re
import shutil
import subprocess
import tempfile
from pathlib import Path
from typing import Any

import numpy as np
import PIL
import pypdf
import skimage
from PIL import Image
from skimage.metrics import structural_similarity

EXPECTED_POPPLER_VERSION = "25.07.0"
INK_THRESHOLD = 245
MIN_INK_RATIO = 0.0005
EDGE_MARGIN = 0.005
MIN_BOUNDING_BOX_AREA_RATIO = 0.90
MIN_SSIM = 0.98


def find_pdftoppm() -> str:
    executable = shutil.which("pdftoppm")
    if executable:
        return executable
    if platform.system() == "Windows":
        packages = Path.home() / "AppData/Local/Microsoft/WinGet/Packages"
        matches = sorted(packages.glob("oschwartz10612.Poppler_*/poppler-25.07.0/Library/bin/pdftoppm.exe"))
        if matches:
            return str(matches[-1])
    raise FileNotFoundError("pdftoppm 25.07.0 is required")


def poppler_version(executable: str) -> str:
    result = subprocess.run(
        [executable, "-v"], capture_output=True, check=True, text=True, encoding="utf-8"
    )
    output = f"{result.stdout}\n{result.stderr}"
    match = re.search(r"pdftoppm version ([0-9.]+)", output)
    if not match:
        raise RuntimeError("Unable to read pdftoppm version")
    version = match.group(1)
    if version != EXPECTED_POPPLER_VERSION:
        raise RuntimeError(f"Expected pdftoppm {EXPECTED_POPPLER_VERSION}, found {version}")
    return version


def render_pages(executable: str, pdf: Path, dpi: int, output: Path) -> list[Path]:
    output.mkdir(parents=True, exist_ok=True)
    prefix = output / pdf.stem
    subprocess.run(
        [executable, "-r", str(dpi), "-gray", "-png", str(pdf), str(prefix)],
        capture_output=True,
        check=True,
        text=True,
        encoding="utf-8",
    )
    return sorted(output.glob(f"{pdf.stem}-*.png"))


def dimensions(pdf: Path) -> list[tuple[float, float]]:
    reader = pypdf.PdfReader(str(pdf))
    return [
        (float(page.mediabox.width), float(page.mediabox.height))
        for page in reader.pages
    ]


def image_metrics(path: Path) -> tuple[np.ndarray[Any, Any], float, list[float] | None]:
    pixels = np.asarray(Image.open(path).convert("L"))
    mask = pixels < INK_THRESHOLD
    ink_ratio = float(mask.mean())
    positions = np.argwhere(mask)
    if positions.size == 0:
        return pixels, ink_ratio, None
    y0, x0 = positions.min(axis=0)
    y1, x1 = positions.max(axis=0)
    height, width = pixels.shape
    return pixels, ink_ratio, [
        round(float(x0 / width), 6),
        round(float(y0 / height), 6),
        round(float((x1 + 1) / width), 6),
        round(float((y1 + 1) / height), 6),
    ]


def bbox_area(box: list[float] | None) -> float:
    if box is None:
        return 0.0
    return max(0.0, box[2] - box[0]) * max(0.0, box[3] - box[1])


def compare(source: Path, output: Path, dpi: int) -> dict[str, Any]:
    pdftoppm = find_pdftoppm()
    version = poppler_version(pdftoppm)
    source_dimensions = dimensions(source)
    output_dimensions = dimensions(output)
    page_reports: list[dict[str, Any]] = []
    with tempfile.TemporaryDirectory(prefix="raster-oracle-") as temporary:
        root = Path(temporary)
        source_pages = render_pages(pdftoppm, source, dpi, root / "source")
        output_pages = render_pages(pdftoppm, output, dpi, root / "output")
        page_count_match = len(source_pages) == len(output_pages)
        for index in range(max(len(source_pages), len(output_pages))):
            if index >= len(source_pages) or index >= len(output_pages):
                page_reports.append(
                    {
                        "pageIndex": index + 1,
                        "orderMatched": False,
                        "sourcePointDimensions": source_dimensions[index] if index < len(source_dimensions) else None,
                        "outputPointDimensions": output_dimensions[index] if index < len(output_dimensions) else None,
                        "dpi": dpi,
                        "ssim": 0.0,
                        "sourceInkRatio": None,
                        "outputInkRatio": None,
                        "sourceBoundingBox": None,
                        "outputBoundingBox": None,
                        "dimensionVerdict": False,
                        "ssimVerdict": False,
                        "blankVerdict": False,
                        "cropVerdict": False,
                        "verdict": False,
                    }
                )
                continue
            source_pixels, source_ink, source_box = image_metrics(source_pages[index])
            output_pixels, output_ink, output_box = image_metrics(output_pages[index])
            same_shape = source_pixels.shape == output_pixels.shape
            score = (
                float(
                    structural_similarity(
                        source_pixels,
                        output_pixels,
                        gaussian_weights=True,
                        sigma=1.5,
                        win_size=11,
                        use_sample_covariance=False,
                        data_range=255,
                    )
                )
                if same_shape
                else 0.0
            )
            source_points = source_dimensions[index]
            output_points = output_dimensions[index]
            dimension_ok = (
                abs(source_points[0] - output_points[0]) <= 1.0
                and abs(source_points[1] - output_points[1]) <= 1.0
            )
            blank_ok = not (source_ink >= MIN_INK_RATIO and output_ink < MIN_INK_RATIO)
            output_touches_edge = (
                output_box is not None
                and (
                    output_box[0] <= EDGE_MARGIN
                    or output_box[1] <= EDGE_MARGIN
                    or output_box[2] >= 1.0 - EDGE_MARGIN
                    or output_box[3] >= 1.0 - EDGE_MARGIN
                )
                and source_box is not None
                and not (
                    source_box[0] <= EDGE_MARGIN
                    or source_box[1] <= EDGE_MARGIN
                    or source_box[2] >= 1.0 - EDGE_MARGIN
                    or source_box[3] >= 1.0 - EDGE_MARGIN
                )
            )
            source_area = bbox_area(source_box)
            crop_ok = (
                not output_touches_edge
                and (source_area == 0.0 or bbox_area(output_box) / source_area >= MIN_BOUNDING_BOX_AREA_RATIO)
            )
            ssim_ok = score >= MIN_SSIM
            verdict = dimension_ok and ssim_ok and blank_ok and crop_ok
            page_reports.append(
                {
                    "pageIndex": index + 1,
                    "orderMatched": dimension_ok and ssim_ok,
                    "sourcePointDimensions": [round(source_points[0], 3), round(source_points[1], 3)],
                    "outputPointDimensions": [round(output_points[0], 3), round(output_points[1], 3)],
                    "dpi": dpi,
                    "ssim": round(score, 6),
                    "sourceInkRatio": round(source_ink, 8),
                    "outputInkRatio": round(output_ink, 8),
                    "sourceBoundingBox": source_box,
                    "outputBoundingBox": output_box,
                    "dimensionVerdict": dimension_ok,
                    "ssimVerdict": ssim_ok,
                    "blankVerdict": blank_ok,
                    "cropVerdict": crop_ok,
                    "verdict": verdict,
                }
            )
    verdict = page_count_match and all(page["verdict"] for page in page_reports)
    return {
        "source": source.name,
        "output": output.name,
        "pageCount": {
            "source": len(source_dimensions),
            "output": len(output_dimensions),
            "verdict": page_count_match,
        },
        "pages": page_reports,
        "tools": {
            "pdftoppm": version,
            "scikit-image": skimage.__version__,
            "pypdf": pypdf.__version__,
            "Pillow": PIL.__version__,
        },
        "thresholds": {
            "dpi": dpi,
            "minimumSsim": MIN_SSIM,
            "minimumInkRatio": MIN_INK_RATIO,
            "edgeMargin": EDGE_MARGIN,
            "minimumBoundingBoxAreaRatio": MIN_BOUNDING_BOX_AREA_RATIO,
        },
        "verdict": verdict,
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("source", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--dpi", type=int, default=150)
    parser.add_argument("--expect", choices=("pass", "fail"))
    arguments = parser.parse_args()
    report = compare(arguments.source, arguments.output, arguments.dpi)
    print(json.dumps(report, indent=2))
    if arguments.expect is None:
        return 0
    expected = arguments.expect == "pass"
    return 0 if report["verdict"] is expected else 1


if __name__ == "__main__":
    raise SystemExit(main())
