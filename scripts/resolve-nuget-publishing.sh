#!/usr/bin/env bash
set -euo pipefail

should_publish="${SHOULD_PUBLISH:-false}"
nuget_user="${NUGET_USER:-}"

if [[ "$should_publish" != "true" && "$should_publish" != "false" ]]; then
  echo "::error::SHOULD_PUBLISH must be 'true' or 'false', got '$should_publish'."
  exit 1
fi

trimmed_user="${nuget_user#"${nuget_user%%[![:space:]]*}"}"
trimmed_user="${trimmed_user%"${trimmed_user##*[![:space:]]}"}"

if [[ "$should_publish" != "true" ]]; then
  echo 'NuGet publication disabled: release publication gate is disabled.'

  if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
    {
      echo 'nuget_publishing_enabled=false'
      echo 'nuget_publishing_reason=release-disabled'
    } >> "$GITHUB_OUTPUT"
  else
    echo 'nuget_publishing_enabled=false'
    echo 'nuget_publishing_reason=release-disabled'
  fi

  exit 0
fi

if [[ -z "$trimmed_user" ]]; then
  echo '::error::Official publication requires repository variable NUGET_USER and a matching NuGet.org Trusted Publishing policy.'
  exit 2
fi

echo 'NuGet publication enabled: release publication is requested and NUGET_USER is configured.'

if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
  {
    echo 'nuget_publishing_enabled=true'
    echo 'nuget_publishing_reason=enabled'
  } >> "$GITHUB_OUTPUT"
else
  echo 'nuget_publishing_enabled=true'
  echo 'nuget_publishing_reason=enabled'
fi
