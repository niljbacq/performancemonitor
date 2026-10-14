#!/usr/bin/env bash
# probe_gpu_linux.sh — verifies (a) lspci name lookup path, (b) hwmon temperature read,
# (c) the exact C# helper logic for [SupportedOSPlatform("linux")] GPU detection.
# READ-ONLY probe: no changes to the repo or system.

set -u
DRM_CARD="${1:-/sys/class/drm/card1}"
DEV="$DRM_CARD/device"

echo "=== 1. lspci -D (raw) ==="
lspci -D 2>/dev/null | grep -iE "vga|3d controller|display controller" || echo "lspci unavailable"

echo
echo "=== 2. DRM device PCI slot ==="
FULL=$(readlink -f "$DEV")
echo "resolved device path: $FULL"
BASENAME=$(basename "$FULL")
echo "basename (slot): '$BASENAME'"

echo
echo "=== 3. hwmon temperature files ==="
if [ -d "$DEV/hwmon" ]; then
    for h in "$DEV"/hwmon/hwmon*; do
        [ -d "$h" ] || continue
        for tf in "$h"/temp*_input; do
            [ -r "$tf" ] || continue
            echo "temp file: $tf = $(cat "$tf" 2>/dev/null || echo unreadable) (milli-C)"
        done
    done
else
    echo "no hwmon dir at $DEV/hwmon"
fi

echo
echo "=== 4. GPU busy / VRAM sysfs files ==="
for f in gpu_busy_percent mem_info_vram_used mem_info_vram_total mem_info_gtt_used mem_info_gtt_total; do
    P="$DEV/$f"
    if [ -r "$P" ]; then echo "$f = $(cat "$P")"; else echo "$f = MISSING/unreadable"; fi
done

echo
echo "=== 5. driver (for IsFramebufferDriver check) ==="
if [ -L "$DEV/driver" ]; then
    echo "driver symlink -> $(readlink "$DEV/driver")"
    echo "driver name: $(basename "$(readlink -f "$DEV/driver")")"
else
    echo "no driver symlink"
fi

echo
echo "=== 6. debugfs presence (TryDebugfsLoad path) ==="
ls /sys/kernel/debug/dri 2>/dev/null || echo "/sys/kernel/debug/dri not accessible"
