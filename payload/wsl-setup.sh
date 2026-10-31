#!/usr/bin/env bash
# Sets up the "LLARP" WSL distro for LLARP Control for Windows. Runs inside it as root;
# the Setup tab of the Windows app calls it with the files it carries:
#
#   wsl-setup.sh PAYLOAD_DIR [--force-build | --files-only]
#
# 1. installs build dependencies and builds llarpd (skipped when this version is installed)
# 2. installs llarpctl + llarp_core.py in /opt/llarp-control (command: /usr/local/bin/llarpctl)
# 3. generates a config, adds the opennet bootstrap and keeps the .loki address permanent
set -euo pipefail

PAYLOAD=${1:?usage: wsl-setup.sh PAYLOAD_DIR [--force-build | --files-only]}
MODE=${2:-}
LLARP_REPO=https://github.com/majestrate/llarp.git
LLARP_COMMIT=$(tr -d '[:space:]' < "$PAYLOAD/LLARP_COMMIT")
TAG=${LLARP_COMMIT:0:9}
SRC=/opt/llarp-src
APP=/opt/llarp-control
LLARPD=/usr/local/bin/llarpd

step() { printf '\n==> %s\n' "$*"; }
warn() { printf 'warning: %s\n' "$*" >&2; }
die() { printf 'error: %s\n' "$*" >&2; exit 1; }

[ "$(id -u)" = 0 ] || die "run as root (wsl -d LLARP -u root)"
grep -qi microsoft /proc/sys/kernel/osrelease || warn "this doesn't look like WSL"
[ -c /dev/net/tun ] || warn "/dev/net/tun is missing; llarpd needs WSL 2 (wsl --set-version LLARP 2)"

# ---------------------------------------------------------------- 1. llarpd
if [ "$MODE" != --files-only ]; then
    step "Installing packages"
    export DEBIAN_FRONTEND=noninteractive
    apt-get update -q
    apt-get install -y -q --no-install-recommends \
        build-essential cmake ninja-build git pkg-config ca-certificates file \
        libuv1-dev libsodium-dev libjemalloc-dev libssl-dev libgeoip-dev \
        python3 iproute2 iptables procps

    if [ "$MODE" != --force-build ] && [ -x "$LLARPD" ] && "$LLARPD" --version 2>/dev/null | grep -q -- "$TAG"; then
        step "llarpd $TAG is already installed; skipping the build"
    else
        step "Fetching llarp $TAG"
        if [ -d "$SRC/.git" ]; then
            git -C "$SRC" fetch -q origin
        else
            rm -rf "$SRC"
            git clone -q "$LLARP_REPO" "$SRC"
        fi
        git -C "$SRC" checkout -q -f "$LLARP_COMMIT"
        git -C "$SRC" submodule update -q --init --recursive

        # each compiler process can use ~1.5 GB; WSL gets half the PC's memory by default
        mem_kb=$(awk '/MemAvailable/ {print $2}' /proc/meminfo)
        jobs=$(( mem_kb / 1500000 )); [ "$jobs" -lt 1 ] && jobs=1
        [ "$jobs" -gt "$(nproc)" ] && jobs=$(nproc)

        step "Building llarpd with $jobs job(s); this takes a while (10-30 minutes)"
        # NATIVE_BUILD=OFF: keep the binary working if the distro is moved to another PC
        cmake -S "$SRC" -B "$SRC/build" -G Ninja \
            -DCMAKE_BUILD_TYPE=Release \
            -DNATIVE_BUILD=OFF \
            -DWITH_SETCAP=OFF \
            -DWITH_SYSTEMD=OFF \
            -DWITH_TESTS=OFF \
            -DBUILD_SHARED_LIBS=OFF \
            -DLLARP_VERSIONTAG="$TAG"
        ninja -C "$SRC/build" -j "$jobs"

        step "Installing llarpd"
        pgrep -x llarpd >/dev/null && warn "llarpd is running; it keeps the old binary until restarted"
        cmake --install "$SRC/build" --prefix /usr/local
        strip "$LLARPD" || true
    fi
    "$LLARPD" --version
fi

# ---------------------------------------------------------------- 2. llarpctl
step "Installing llarpctl"
install -d "$APP/bootstrap"
# the payload was written on Windows; make sure no CR line endings slipped in
tr -d '\r' < "$PAYLOAD/llarp_core.py" > "$APP/llarp_core.py"
tr -d '\r' < "$PAYLOAD/llarpctl" > "$APP/llarpctl"
chmod 644 "$APP/llarp_core.py"
chmod 755 "$APP/llarpctl"
install -m 644 "$PAYLOAD/opennet.signed" "$APP/bootstrap/"
ln -sf "$APP/llarpctl" /usr/local/bin/llarpctl
rm -rf "$APP/__pycache__"

# ---------------------------------------------------------------- 3. configure
[ -x "$LLARPD" ] || { warn "llarpd isn't built yet; run the full setup"; exit 0; }
step "Configuring"
llarpctl set llarpd "$LLARPD" >/dev/null
CONFIG=$(llarpctl set config)
if [ -f "$CONFIG" ]; then
    echo "using existing config $CONFIG"
else
    mkdir -p "$(dirname "$CONFIG")"
    llarpctl generate-config | tail -1
fi
if [[ $(llarpctl bootstrap list) == *opennet.signed* ]]; then
    echo "opennet bootstrap already added"
else
    llarpctl bootstrap add opennet
fi
if [[ $(llarpctl persist) != *" on" ]]; then
    llarpctl persist on
fi

step "Done"
llarpctl status || true
