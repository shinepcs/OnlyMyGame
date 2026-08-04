#!/usr/bin/env python3
"""Print the deterministic identity of every source shipped in an API release."""

from __future__ import annotations

import hashlib
import argparse
from pathlib import Path
import subprocess


SOURCE_SCOPES = (
    "Server/OnlyMyGame.Api",
    "Assets/OnlyMyGame/Core/RuleCore.cs",
    "Assets/OnlyMyGame/Core/RuleExpressions.cs",
    "Assets/OnlyMyGame/Core/DynamicActionTargeting.cs",
    "docker-compose.yml",
)


def repository_root() -> Path:
    result = subprocess.run(
        ("git", "rev-parse", "--show-toplevel"),
        check=True,
        stdout=subprocess.PIPE,
        text=True,
    )
    return Path(result.stdout.strip()).resolve()


def tracked_release_files(root: Path) -> list[str]:
    result = subprocess.run(
        ("git", "-C", str(root), "ls-files", "-z", "--", *SOURCE_SCOPES),
        check=True,
        stdout=subprocess.PIPE,
    )
    paths = sorted(path.decode("utf-8") for path in result.stdout.split(b"\0") if path)
    if not paths:
        raise SystemExit("API source identity failed: release source set is empty.")
    return paths


def committed_release_blobs(root: Path, revision: str) -> list[tuple[str, str]]:
    result = subprocess.run(
        (
            "git",
            "-C",
            str(root),
            "ls-tree",
            "-r",
            "-z",
            "--full-tree",
            revision,
            "--",
            *SOURCE_SCOPES,
        ),
        check=True,
        stdout=subprocess.PIPE,
    )
    blobs: list[tuple[str, str]] = []
    for raw_entry in result.stdout.split(b"\0"):
        if not raw_entry:
            continue
        metadata, raw_path = raw_entry.split(b"\t", 1)
        mode, object_type, object_id = metadata.decode("ascii").split(" ")
        relative = raw_path.decode("utf-8")
        if object_type != "blob" or mode not in {"100644", "100755"}:
            raise SystemExit(f"API source identity failed: unsafe committed file {relative}.")
        blobs.append((relative, object_id))
    blobs.sort(key=lambda item: item[0])
    if not blobs:
        raise SystemExit("API source identity failed: committed release source set is empty.")
    return blobs


def committed_blob(root: Path, object_id: str) -> bytes:
    result = subprocess.run(
        ("git", "-C", str(root), "cat-file", "blob", object_id),
        check=True,
        stdout=subprocess.PIPE,
    )
    return result.stdout


def resolve_commit(root: Path, revision: str) -> str:
    result = subprocess.run(
        ("git", "-C", str(root), "rev-parse", "--verify", f"{revision}^{{commit}}"),
        check=True,
        stdout=subprocess.PIPE,
        text=True,
    )
    commit = result.stdout.strip()
    if len(commit) != 40 or any(character not in "0123456789abcdef" for character in commit):
        raise SystemExit("API source identity failed: revision did not resolve to a full commit SHA.")
    return commit


def compute_source_hash(root: Path, revision: str | None = None) -> str:
    identity = hashlib.sha256(b"onlymygame-api-source-v1\0")
    if revision is not None:
        commit = resolve_commit(root, revision)
        sources = ((relative, committed_blob(root, object_id)) for relative, object_id in committed_release_blobs(root, commit))
    else:
        def working_tree_sources():
            for relative in tracked_release_files(root):
                path = root / relative
                if not path.is_file() or path.is_symlink():
                    raise SystemExit(f"API source identity failed: unsafe tracked file {relative}.")
                yield relative, path.read_bytes()

        sources = working_tree_sources()

    for relative, contents in sources:
        identity.update(relative.encode("utf-8"))
        identity.update(b"\0")
        identity.update(hashlib.sha256(contents).digest())
    return identity.hexdigest()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--commit", help="hash blobs from this immutable Git commit instead of the working tree")
    arguments = parser.parse_args()
    print(compute_source_hash(repository_root(), arguments.commit))
