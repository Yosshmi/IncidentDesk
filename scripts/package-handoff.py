#!/usr/bin/env python3
"""Package a clean committed repository with portable, sanitized Git history."""

import argparse
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import zipfile


NAME = "Yosshmi"
EMAIL = "yosshmi04@gmail.com"


def git(repository, *arguments):
    environment = os.environ.copy()
    environment["GIT_CONFIG_NOSYSTEM"] = "1"
    environment["GIT_CONFIG_GLOBAL"] = os.devnull
    return subprocess.run(["git", "-C", str(repository), *arguments], check=True,
                          capture_output=True, text=True, env=environment).stdout.strip()


def verify_identity(repository):
    for entry in git(repository, "log", "--all", "--format=%an%x00%ae%x00%cn%x00%ce").splitlines():
        if entry.split("\0") != [NAME, EMAIL, NAME, EMAIL]:
            raise RuntimeError("All author and committer identities must be Yosshmi <yosshmi04@gmail.com>.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, help="ZIP path outside the repository")
    parser.add_argument("--force", action="store_true", help="Replace an existing output ZIP")
    args = parser.parse_args()
    repository = Path(__file__).resolve().parents[1]
    output = (args.output or repository.parent / "IncidentDesk-handoff.zip").expanduser().resolve()
    if output == repository or repository in output.parents:
        raise RuntimeError("The handoff ZIP must be written outside the repository.")
    if output.exists() and not args.force:
        raise RuntimeError(f"Output already exists: {output}. Use --force to replace it.")
    if git(repository, "status", "--porcelain", "--untracked-files=normal"):
        raise RuntimeError("Commit all intended changes and ensure git status is clean before packaging.")
    if git(repository, "branch", "--show-current") != "main":
        raise RuntimeError("Check out main before packaging the handoff.")
    if git(repository, "rev-parse", "--is-shallow-repository") != "false":
        raise RuntimeError("A shallow repository cannot provide a complete-history handoff.")
    verify_identity(repository)
    head = git(repository, "rev-parse", "HEAD")
    for filename in git(repository, "ls-files", "-z").split("\0"):
        path = Path(filename)
        if path.name == ".env" or (path.name.startswith(".env.") and path.name != ".env.example") or path.suffix in {".pem", ".key", ".pfx"}:
            raise RuntimeError(f"Refusing to package a potential secret file: {filename}")

    output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="incidentdesk-package-") as temporary:
        temporary_root = Path(temporary)
        staged = temporary_root / "IncidentDesk"
        # A transport clone copies reachable history, without local hardlinks, ignored
        # files, reflogs, hooks, credentials, or an objects/info/alternates dependency.
        git(repository, "-c", "init.templateDir=", "clone", "--quiet", "--no-local",
            "--no-hardlinks", str(repository), str(staged))
        for branch in git(repository, "for-each-ref", "--format=%(refname) %(objectname)", "refs/heads").splitlines():
            reference, commit = branch.split(" ", 1)
            git(staged, "update-ref", reference, commit)
        git(staged, "remote", "remove", "origin")
        (staged / ".git" / "config").write_text(
            "[core]\n\trepositoryformatversion = 0\n\tfilemode = true\n\tbare = false\n"
            "\tlogallrefupdates = true\n[user]\n\tname = Yosshmi\n\temail = yosshmi04@gmail.com\n"
            "[commit]\n\tgpgsign = false\n", encoding="utf-8")
        for directory in ("hooks", "logs"):
            shutil.rmtree(staged / ".git" / directory, ignore_errors=True)
        for filename in ("FETCH_HEAD", "ORIG_HEAD"):
            (staged / ".git" / filename).unlink(missing_ok=True)
        git(staged, "fsck", "--full")
        if git(staged, "status", "--porcelain") or git(staged, "rev-parse", "HEAD") != head:
            raise RuntimeError("Staged repository failed the clean-history verification.")

        temporary_zip = temporary_root / "handoff.zip"
        with zipfile.ZipFile(temporary_zip, "w", zipfile.ZIP_DEFLATED) as archive:
            for path in sorted(staged.rglob("*")):
                if path.is_symlink():
                    raise RuntimeError(f"Symlinks require an explicit packaging review: {path.relative_to(staged)}")
                if path.is_file():
                    archive.write(path, Path("IncidentDesk") / path.relative_to(staged))

        extracted = temporary_root / "verification"
        with zipfile.ZipFile(temporary_zip) as archive:
            archive.extractall(extracted)
            for entry in archive.infolist():
                mode = (entry.external_attr >> 16) & 0o777
                if mode:
                    (extracted / entry.filename).chmod(mode)
        restored = extracted / "IncidentDesk"
        git(restored, "fsck", "--full")
        verify_identity(restored)
        if git(restored, "rev-parse", "HEAD") != head or git(restored, "status", "--porcelain") or git(restored, "remote"):
            raise RuntimeError("The extracted handoff failed Git verification.")
        shutil.copyfile(temporary_zip, output)
    print(f"Created {output} ({output.stat().st_size:,} bytes).")
    print(f"Verified main at {head}, clean working tree, complete reachable history, author/committer identity, and no remote.")
    print("Ignored runtime files are excluded. Review committed history for secrets before sharing.")


if __name__ == "__main__":
    try:
        main()
    except (RuntimeError, subprocess.CalledProcessError) as error:
        raise SystemExit(f"Packaging failed: {error}") from error
