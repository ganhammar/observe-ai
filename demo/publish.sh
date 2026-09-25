#!/usr/bin/env bash
# Publishes demo/pricing-demo/ as the root of an existing repository's main
# branch. The repository only ever holds a copy of that folder, so replacing
# its history on every publish loses nothing.
#
# Usage: demo/publish.sh [owner/name]   (default: $GITHUB_ORG/pricing-demo)
set -euo pipefail

target="${1:-${GITHUB_ORG:-ganhammar}/pricing-demo}"
here="$(cd "$(dirname "$0")" && pwd)"

if ! gh repo view "$target" >/dev/null 2>&1; then
  echo "Repository $target does not exist. Create it first, for example:" >&2
  echo "  gh repo create $target --public --description 'Demo service for observe-ai'" >&2
  exit 1
fi

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

cp -R "$here/pricing-demo/." "$work/"
git -C "$work" init -q -b main
git -C "$work" add -A
git -C "$work" commit -q -m "chore: publish demo service"
git -C "$work" -c credential.helper="!gh auth git-credential" push -q --force "https://github.com/$target.git" main

echo "https://github.com/$target"
