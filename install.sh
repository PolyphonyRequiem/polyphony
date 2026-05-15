#!/usr/bin/env bash
#
# One-shot operator install for polyphony on Linux / macOS.
#
# Recommended invocation:
#   curl -fsSL https://raw.githubusercontent.com/PolyphonyRequiem/polyphony/main/install.sh | bash
#
# Pin to a specific release:
#   curl -fsSL https://raw.githubusercontent.com/PolyphonyRequiem/polyphony/main/install.sh | bash -s v2.4.0
#
# Idempotent: safe to re-run; overwrites in place. Does NOT install git, pwsh,
# or conductor — warns if missing but never invokes a package manager.

set -euo pipefail

VERSION="${1:-latest}"

echo "==> polyphony installer"

# ── Prereq check (warn-only) ─────────────────────────────────────────────────
missing=()
for cmd in git pwsh; do
    if ! command -v "$cmd" >/dev/null 2>&1; then missing+=("$cmd"); fi
done
conductor_present=true
if ! command -v conductor >/dev/null 2>&1; then conductor_present=false; fi

if [ "${#missing[@]}" -gt 0 ]; then
    echo ""
    echo "WARN: missing required commands: ${missing[*]}"
    echo "      install them before running polyphony:"
    for c in "${missing[@]}"; do
        case "$c" in
            git)  echo "        git:  OS package manager (apt/brew/dnf)" ;;
            pwsh) echo "        pwsh: https://github.com/PowerShell/PowerShell" ;;
        esac
    done
fi
if [ "$conductor_present" = false ]; then
    echo ""
    echo "WARN: 'conductor' not found on PATH."
    echo "      install with: pip install \"git+https://github.com/microsoft/conductor.git@main\""
fi

# ── Resolve release tag ──────────────────────────────────────────────────────
if [ "$VERSION" = "latest" ]; then
    echo "==> resolving latest release..."
    tag=$(curl -fsSL https://api.github.com/repos/PolyphonyRequiem/polyphony/releases/latest \
          | sed -nE 's/.*"tag_name"[[:space:]]*:[[:space:]]*"([^"]+)".*/\1/p' | head -n1)
else
    case "$VERSION" in v*) tag="$VERSION" ;; *) tag="v$VERSION" ;; esac
fi
ver="${tag#v}"
echo "    using release: $tag"

# ── Pick RID for the host ────────────────────────────────────────────────────
case "$(uname -s)/$(uname -m)" in
    Linux/x86_64)  rid=linux-x64 ;;
    Darwin/arm64)  rid=osx-arm64 ;;
    Darwin/x86_64) rid=osx-x64 ;;
    *) echo "ERROR: unsupported host: $(uname -s)/$(uname -m)" >&2; exit 1 ;;
esac

asset="polyphony-${ver}-${rid}"
base="https://github.com/PolyphonyRequiem/polyphony/releases/download/${tag}"
install_dir="$HOME/.polyphony/bin"
mkdir -p "$install_dir"

# Migration warning: the canonical location moved from ~/.twig/bin to
# ~/.polyphony/bin. Surface a legacy install if present.
legacy_install="$HOME/.twig/bin/polyphony"
if [ -f "$legacy_install" ]; then
    echo ""
    echo "==> NOTE: legacy install found at $legacy_install"
    echo "    The canonical install location is now ~/.polyphony/bin/."
    echo "    After this install, verify \`command -v polyphony\` resolves to"
    echo "    the new location, then remove the legacy copy:"
    echo "      rm '$legacy_install'"
    echo ""
fi

# ── Download + verify binary ─────────────────────────────────────────────────
tmpdir=$(mktemp -d)
trap 'rm -rf "$tmpdir"' EXIT

echo "==> downloading binary ($asset)..."
curl -fsSL -o "$tmpdir/$asset" "${base}/${asset}"
curl -fsSL -o "$tmpdir/${asset}.sha256" "${base}/${asset}.sha256"

(cd "$tmpdir" && shasum -a 256 -c "${asset}.sha256")
chmod +x "$tmpdir/$asset"
mv "$tmpdir/$asset" "$install_dir/polyphony"

# ── Download launcher scripts ────────────────────────────────────────────────
# THE GAP: launcher scripts aren't bundled as release assets yet — fetched
# from main HEAD. Drifts vs binary version; acceptable until next release.
launcher_base='https://raw.githubusercontent.com/PolyphonyRequiem/polyphony/main/scripts'
echo "==> downloading launcher scripts..."
for s in Invoke-PolyphonySdlc.ps1 Resolve-GhIdentity.ps1 Twig-Hydration.ps1 Migrate-ToBareRepo.ps1; do
    curl -fsSL -o "$install_dir/$s" "$launcher_base/$s"
done

# ── Ensure ~/.polyphony/bin on PATH ─────────────────────────────────────────
if [[ ":$PATH:" != *":$install_dir:"* ]]; then
    echo "==> adding $install_dir to PATH (in ~/.zshrc and ~/.bashrc if present)..."
    for rc in "$HOME/.zshrc" "$HOME/.bashrc"; do
        if [ -f "$rc" ] && ! grep -q "\.polyphony/bin" "$rc"; then
            echo 'export PATH="$HOME/.polyphony/bin:$PATH"' >> "$rc"
        fi
    done
    export PATH="$install_dir:$PATH"
fi

# ── Install both copilot skills user-global ─────────────────────────────────
skills_dir="$HOME/.copilot/skills"
mkdir -p "$skills_dir"
skill_base='https://raw.githubusercontent.com/PolyphonyRequiem/polyphony/main/.github/skills'

echo "==> installing copilot skills (polyphony-runtime, polyphony-bootstrap)..."
for skill in polyphony-runtime polyphony-bootstrap; do
    mkdir -p "$skills_dir/$skill"
    curl -fsSL -o "$skills_dir/$skill/SKILL.md" "$skill_base/$skill/SKILL.md"
done
mkdir -p "$skills_dir/polyphony-runtime/templates"
curl -fsSL -o "$skills_dir/polyphony-runtime/templates/target-repo-stub.md" \
    "$skill_base/polyphony-runtime/templates/target-repo-stub.md"

# ── Verify install resolved correctly ────────────────────────────────────────
resolved="$(command -v polyphony)"
if [ "$resolved" != "$install_dir/polyphony" ]; then
    echo "ERROR: polyphony resolves to '$resolved', expected '$install_dir/polyphony'." >&2
    exit 1
fi
echo ""
echo "==> install complete"
polyphony --version

# ── Next steps ───────────────────────────────────────────────────────────────
echo ""
echo "Next steps:"
if [ "${#missing[@]}" -gt 0 ] || [ "$conductor_present" = false ]; then
    echo "  1. Install missing prereqs (see warnings above)."
fi
echo "  - Register polyphony with conductor (one-time, per machine):"
echo "      conductor registry add polyphony PolyphonyRequiem/polyphony"
echo "  - Open a copilot CLI session in any repo and ask:"
echo "      'set up polyphony for this repo'   (triggers polyphony-bootstrap)"
echo "      'run polyphony for work item N'    (triggers polyphony-runtime)"
