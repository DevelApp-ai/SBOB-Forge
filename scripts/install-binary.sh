#!/usr/bin/env bash
# SBOB-Registry #27 — install a single-file self-contained sbobforge binary.
# Shared by the sbobforge action's `binary: true` mode and usable standalone
# (call it; the absolute binary path is the last line on stdout).
#
# Asset resolution:
#   SBORGFORGE_BINARY_DIR set   — use assets from that local directory instead
#     of downloading (CI smoke lanes / air-gapped use). SBORGFORGE_VERSION
#     must still be set so the checksum file can be located.
#   SBORGFORGE_BINARY_DIR unset — download from the SBOB-Forge release named
#     by SBORGFORGE_VERSION (v-prefix optional); an empty SBORGFORGE_VERSION
#     resolves the latest published release.
#
# Fail-closed: the asset's sha256 is verified against the release checksum
# file before anything is executed. This is also the seam where the #26
# cosign verification slots in (bundle + identity pin before the checksum).
#
# Exit codes: 0 ok; 1 usage; 2 asset/checksum entry not found;
# 3 download/extract failure; 4 checksum mismatch; 5 unsupported platform.

set -euo pipefail

REPO_SLUG="DevelApp-ai/SBOB-Forge"
DEST="${SBORGFORGE_BINARY_DEST:-"${RUNNER_TEMP:-/tmp}/sbobforge-bin"}"

fail() { local code="$1"; shift; echo "install-binary: $*" >&2; exit "$code"; }

# --- platform → asset mapping (single-file self-contained, leaf #27) ------
case "$(uname -s) $(uname -m)" in
  "Linux x86_64")              PLATFORM="linux-x64" ;;
  "Linux aarch64")             PLATFORM="linux-arm64" ;;
  MINGW64*\ x86_64 | MSYS*\ x86_64 | CYGWIN*\ x86_64)
                               PLATFORM="win-x64" ;;
  *) fail 5 "unsupported platform: $(uname -s) $(uname -m) — published binaries are linux-x64, linux-arm64, win-x64" ;;
esac

VERSION="${SBORGFORGE_VERSION:-}"
VERSION="${VERSION#v}"

if [ -n "${SBORGFORGE_BINARY_DIR:-}" ]; then
  [ -n "$VERSION" ] || fail 1 "SBORGFORGE_BINARY_DIR set but SBORGFORGE_VERSION empty — cannot locate the checksum file"
  ASSET_DIR="$SBORGFORGE_BINARY_DIR"
  ASSET="$ASSET_DIR/sbobforge-$VERSION-$PLATFORM.$([ "$PLATFORM" = win-x64 ] && echo zip || echo tar.gz)"
  CHECKSUMS="$ASSET_DIR/sbobforge-$VERSION-checksums.txt"
  [ -f "$ASSET" ] || fail 2 "local asset missing: $ASSET"
  [ -f "$CHECKSUMS" ] || fail 2 "local checksum file missing: $CHECKSUMS"
else
  if [ -z "$VERSION" ]; then
    # resolve the latest release tag via the redirect — no API call, no auth
    VERSION="$(curl -fsSI "https://github.com/$REPO_SLUG/releases/latest" \
      | tr -d '\r' \
      | awk 'tolower($1)=="location:" {print $2}' \
      | sed 's#.*/tag/v##;s#/releases/tag/.*##')" \
      || fail 3 "could not resolve the latest sbobforge release"
    [ -n "$VERSION" ] || fail 3 "could not parse the latest sbobforge release tag"
  fi
  mkdir -p "$DEST"
  ASSET_NAME="sbobforge-$VERSION-$PLATFORM.$([ "$PLATFORM" = win-x64 ] && echo zip || echo tar.gz)"
  CHECKSUMS_NAME="sbobforge-$VERSION-checksums.txt"
  ASSET="$DEST/$ASSET_NAME"
  CHECKSUMS="$DEST/$CHECKSUMS_NAME"
  curl -fsSL -o "$ASSET" \
    "https://github.com/$REPO_SLUG/releases/download/v$VERSION/$ASSET_NAME" \
    || fail 3 "download failed: $ASSET_NAME (release v$VERSION)"
  curl -fsSL -o "$CHECKSUMS" \
    "https://github.com/$REPO_SLUG/releases/download/v$VERSION/$CHECKSUMS_NAME" \
    || fail 3 "download failed: $CHECKSUMS_NAME (release v$VERSION)"
fi

ASSET_NAME="$(basename "$ASSET")"

# --- fail-closed checksum verification before anything is executed ---------
( cd "$(dirname "$ASSET")" \
  && grep -E "[[:space:]]${ASSET_NAME}\$" "$(basename "$CHECKSUMS")" >/dev/null \
  ) || fail 2 "asset not listed in checksum file: $ASSET_NAME"
( cd "$(dirname "$ASSET")" \
  && grep -E "[[:space:]]${ASSET_NAME}\$" "$(basename "$CHECKSUMS")" \
  | sha256sum -c - >/dev/null \
  ) || fail 4 "checksum mismatch: $ASSET_NAME does not match the published sha256 — do not run it"

# --- extract ----------------------------------------------------------------
mkdir -p "$DEST"
case "$PLATFORM" in
  win-x64) unzip -o "$ASSET" -d "$DEST" >/dev/null || fail 3 "unzip failed: $ASSET"
           BIN="$DEST/sbobforge.exe" ;;
  *)       tar -xzf "$ASSET" -C "$DEST" || fail 3 "tar extraction failed: $ASSET"
           BIN="$DEST/sbobforge" ;;
esac
[ -f "$BIN" ] || fail 3 "binary not found after extract: $BIN"
chmod +x "$BIN"
echo "$BIN"
