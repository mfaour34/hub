#!/usr/bin/env bash
# Builds hub-dsp.dll (x64 Windows, no runtime dependencies) with Zig's bundled clang + mingw.
set -euo pipefail
cd "$(dirname "$0")"
ZIG="${ZIG:-$HOME/tools/zig-linux-x86_64-0.13.0/zig}"
"$ZIG" cc -target x86_64-windows-gnu -shared -O2 -ffast-math -fno-finite-math-only -Wall -Wextra \
  -o hub-dsp.dll dsp_exports.c limiter.c -Wl,--subsystem,windows
rm -f hub-dsp.pdb *.lib
echo "built $(pwd)/hub-dsp.dll"
