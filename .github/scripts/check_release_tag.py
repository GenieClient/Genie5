#!/usr/bin/env python3
"""Release tag guard, run as the first job of release.yml.

Usage: check_release_tag.py <tag> < published-tags.txt

Fails (exit 1) unless <tag> is

  * one of the two allowed shapes:  vX.Y.Z  (stable)  or  vX.Y.Z-beta.N
    (pre-release of the stable version it leads up to), and
  * higher, in semantic-version order, than every tag already published
    (one per line on stdin; the tag itself is ignored so a re-run passes).

Why: a pre-release sorts BELOW its own base version, so a tag such as
v5.0.0-beta.12 pushed after v5.0.0 would never be offered to anyone, and
"beta10" (no dot) sorts as text, below "beta9". Catching either here stops
the run before a GitHub Release exists.

v0.0.x tags are throwaway pipeline tests and are exempt.
"""
import re
import sys

ALLOWED = re.compile(r"^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-beta\.[1-9]\d*)?$")
SEMVER = re.compile(r"^v?(\d+)\.(\d+)\.(\d+)(?:-([0-9A-Za-z.-]+))?(?:\+.*)?$")


def key(tag):
    """Sort key in semantic-version order, or None if the tag is not a version."""
    m = SEMVER.match(tag.strip())
    if not m:
        return None
    core = tuple(int(m.group(i)) for i in (1, 2, 3))
    pre = m.group(4)
    if pre is None:
        # A release sorts above every pre-release of the same core version.
        return core + ((1,),)
    ids = []
    for part in pre.split("."):
        # Numeric identifiers compare as numbers and sort below text ones.
        ids.append((0, int(part), "") if part.isdigit() else (1, 0, part))
    return core + ((0, tuple(ids)),)


def check(tag, published):
    """Return an error message, or None when the tag may be released."""
    if tag.startswith("v0.0."):
        return None
    if not ALLOWED.match(tag):
        return (f"Tag '{tag}' is not an allowed release tag. Use vX.Y.Z for a stable "
                f"release or vX.Y.Z-beta.N for a pre-release (for example v5.1.0 or "
                f"v5.1.0-beta.1).")
    mine = key(tag)
    newest = None
    for other in published:
        other = other.strip()
        if not other or other == tag or other.startswith("v0.0."):
            continue
        k = key(other)
        if k is not None and (newest is None or k > newest[0]):
            newest = (k, other)
    if newest is not None and mine <= newest[0]:
        return (f"Tag '{tag}' does not sort above the newest published release "
                f"'{newest[1]}'. A pre-release is named after the NEXT stable version "
                f"(after v5.0.0 comes v5.0.1-beta.1 or v5.1.0-beta.1, never "
                f"v5.0.0-beta.N), and version numbers only go up.")
    return None


def main(argv):
    if len(argv) != 2:
        print("usage: check_release_tag.py <tag> < published-tags.txt", file=sys.stderr)
        return 2
    error = check(argv[1], sys.stdin.read().splitlines())
    if error:
        print(f"::error::{error}")
        return 1
    print(f"Tag '{argv[1]}' is a valid release tag and sorts above every published release.")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
