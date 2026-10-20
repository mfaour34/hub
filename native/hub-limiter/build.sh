#!/usr/bin/env bash
# Builds hub-limiter.dll (x64 Windows, no runtime dependencies) with Zig's bundled clang + mingw.
set -euo pipefail
cd "$(dirname "$0")"
ZIG="${ZIG:-$HOME/tools/zig-linux-x86_64-0.13.0/zig}"
"$ZIG" cc -target x86_64-windows-gnu -shared -O2 -ffast-math -fno-finite-math-only -Wall -Wextra \
  -o hub-limiter.dll vst2.c limiter.c -Wl,--subsystem,windows
echo "built $(pwd)/hub-limiter.dll"
