#!/usr/bin/env python3
"""Compare PT-BR and EN-US resource dictionaries and report missing keys."""

from __future__ import annotations

import argparse
import re
from pathlib import Path
from typing import Iterable, Sequence, Set

KEY_PATTERN = re.compile(r'Key="([^"]+)"')


def extract_keys(path: Path) -> Set[str]:
    text = path.read_text(encoding="utf-8")
    return set(KEY_PATTERN.findall(text))


def report_missing(base_keys: Set[str], compare_keys: Set[str], base_name: str, compare_name: str) -> int:
    missing = sorted(base_keys - compare_keys)
    if not missing:
        print(f"All keys from {base_name} exist in {compare_name}.")
        return 0

    print(f"Keys present in {base_name} but missing in {compare_name}: {len(missing)}")
    for key in missing:
        print(key)
    return len(missing)


def parse_args(argv: Sequence[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Find missing localization keys between PT-BR and EN-US resource dictionaries."
    )
    parser.add_argument(
        "--pt", default="NXProject.Community/Strings/Strings.pt-BR.xaml",
        help="Path to the Portuguese resource dictionary.")
    parser.add_argument(
        "--en", default="NXProject.Community/Strings/Strings.en-US.xaml",
        help="Path to the English resource dictionary.")
    parser.add_argument(
        "--reverse", action="store_true",
        help="Also report keys present in EN-US but missing in PT-BR.")
    return parser.parse_args(argv)


def main(argv: Sequence[str] | None = None) -> int:
    args = parse_args(argv)
    pt_path = Path(args.pt)
    en_path = Path(args.en)

    if not pt_path.exists():
        print(f"Error: PT-BR file not found: {pt_path}")
        return 1
    if not en_path.exists():
        print(f"Error: EN-US file not found: {en_path}")
        return 1

    pt_keys = extract_keys(pt_path)
    en_keys = extract_keys(en_path)

    print(f"Loaded {len(pt_keys)} keys from {pt_path}")
    print(f"Loaded {len(en_keys)} keys from {en_path}\n")

    missing_count = report_missing(pt_keys, en_keys, "PT-BR", "EN-US")
    if args.reverse:
        print()
        missing_count += report_missing(en_keys, pt_keys, "EN-US", "PT-BR")

    return 0 if missing_count == 0 else 2


if __name__ == "__main__":
    raise SystemExit(main())
