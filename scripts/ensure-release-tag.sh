#!/usr/bin/env bash
set -euo pipefail

release_tag=''
validated_sha=''

while [[ $# -gt 0 ]]; do
  case "$1" in
    --tag)
      release_tag="${2:-}"
      shift 2
      ;;
    --sha)
      validated_sha="${2:-}"
      shift 2
      ;;
    *)
      echo "Unknown argument: $1" >&2
      exit 2
      ;;
  esac
done

if [[ -z "$release_tag" ]]; then
  echo '--tag is required.' >&2
  exit 2
fi

if [[ -z "$validated_sha" ]]; then
  echo '--sha is required.' >&2
  exit 2
fi

git fetch --tags origin

if git rev-parse -q --verify "refs/tags/$release_tag" >/dev/null; then
  existing_sha="$(git rev-list -n 1 "$release_tag")"
  if [[ "$existing_sha" != "$validated_sha" ]]; then
    echo "Tag '$release_tag' points to $existing_sha instead of validated SHA $validated_sha. Refusing to move it." >&2
    exit 3
  fi

  echo "Tag '$release_tag' already points to the validated commit; accepting it."
  if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
    echo 'tag_created=false' >> "$GITHUB_OUTPUT"
  fi
  exit 0
fi

git tag "$release_tag" "$validated_sha"
git push origin "refs/tags/$release_tag"
git fetch --tags origin

actual_sha="$(git rev-list -n 1 "$release_tag")"
if [[ "$actual_sha" != "$validated_sha" ]]; then
  echo "Tag '$release_tag' resolves to $actual_sha instead of validated SHA $validated_sha after creation." >&2
  exit 3
fi

echo "Tag '$release_tag' created at $validated_sha."
if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
  echo 'tag_created=true' >> "$GITHUB_OUTPUT"
fi
