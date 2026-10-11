#!/usr/bin/env bash
set -euo pipefail
export PATH=/mingw64/bin:/usr/bin:/bin
export MSYSTEM=MINGW64
root=$(cygpath -u "$1")
prefix="$root/prefix"
mkdir -p "$prefix" "$root/native"
gcc --version | head -1 | grep -F '15.2.0'
export SOURCE_DATE_EPOCH=1750000000
export CFLAGS="-O2 -g0 -ffile-prefix-map=$root=."
export LDFLAGS='-Wl,--no-insert-timestamp'
cd "$root/libusb-1.0.30"
./configure --host=x86_64-w64-mingw32 --prefix="$prefix" --enable-shared --enable-static
make -j2
make install
cd "$root/dfu-util-0.11"
# The upstream configure script reads USB_*, not LIBUSB_*.
export USB_CFLAGS="-I$prefix/include/libusb-1.0"
export USB_LIBS="$prefix/lib/libusb-1.0.a"
export LDFLAGS='-static -Wl,--no-insert-timestamp'
export PKG_CONFIG_LIBDIR="$prefix/lib/pkgconfig"
./configure --host=x86_64-w64-mingw32 --prefix="$prefix"
make -j2
cp "$prefix/bin/libusb-1.0.dll" "$root/native/libusb-1.0.dll"
cp src/dfu-util.exe "$root/native/dfu-util-static.exe"
strip --strip-unneeded "$root/native/libusb-1.0.dll" "$root/native/dfu-util-static.exe"
for file in "$root/native/libusb-1.0.dll" "$root/native/dfu-util-static.exe"; do
  objdump -f "$file" | grep -F 'pei-x86-64'
  imports=$(objdump -p "$file" | sed -n 's/.*DLL Name: //p')
  test -n "$imports"
  # No external libusb, winpthread or toolchain runtime DLL may be needed.
  while IFS= read -r dependency; do
    case "${dependency,,}" in kernel32.dll|msvcrt.dll) ;; *) printf 'Unexpected import: %s\n' "$dependency" >&2; exit 1 ;; esac
  done <<< "$imports"
done
# --version returns before USB initialization in the pinned dfu-util source.
"$root/native/dfu-util-static.exe" --version
