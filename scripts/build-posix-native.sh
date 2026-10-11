#!/usr/bin/env bash
set -euo pipefail
root=$1
runtime=$2
prefix="$root/prefix"
native="$root/native"
mkdir -p "$prefix" "$native"
export SOURCE_DATE_EPOCH=1750000000
export CFLAGS='-O2 -g0'
export CXXFLAGS='-O2 -g0'
case "$runtime" in
  linux-x64) test "$(uname -s)-$(uname -m)" = 'Linux-x86_64' ;;
  osx-x64) test "$(uname -s)-$(uname -m)" = 'Darwin-x86_64'; export MACOSX_DEPLOYMENT_TARGET=12.0 ;;
  osx-arm64) test "$(uname -s)-$(uname -m)" = 'Darwin-arm64'; export MACOSX_DEPLOYMENT_TARGET=12.0 ;;
  *) printf '%s\n' 'Unsupported native runtime.' >&2; exit 1 ;;
esac

cd "$root/libusb-1.0.30"
options=(--prefix="$prefix" --disable-static --disable-dependency-tracking)
if [[ "$runtime" == linux-* ]]; then options+=(--disable-udev); fi
./configure "${options[@]}"
make -j 2
make install

cd "$root/dfu-util-0.11"
export LIBUSB_CFLAGS="-I$prefix/include/libusb-1.0"
export LIBUSB_LIBS="-L$prefix/lib -lusb-1.0"
export PKG_CONFIG_LIBDIR="$prefix/lib/pkgconfig"
./configure --prefix="$prefix" --disable-dependency-tracking
make -j 2
cp src/dfu-util "$native/dfu-util"
if [[ "$runtime" == linux-* ]]; then
  cp -L "$prefix/lib/libusb-1.0.so.0" "$native/libusb-1.0.so.0"
  patchelf --set-rpath '$ORIGIN' "$native/dfu-util"
  readelf -d "$native/dfu-util" | grep -E '\((RUNPATH|RPATH)\).*\[\$ORIGIN\]'
  if ldd "$native/dfu-util" | grep -q 'not found'; then exit 1; fi
else
  cp -L "$prefix/lib/libusb-1.0.dylib" "$native/libusb-1.0.dylib"
  install_name_tool -id '@rpath/libusb-1.0.dylib' "$native/libusb-1.0.dylib"
  dependency=$(otool -L "$native/dfu-util" | awk '$1 ~ /\/libusb-1\.0[^\/]*\.dylib$/ {print $1}')
  [[ "$dependency" == "$prefix/lib/"* && "$dependency" != *$'\n'* ]]
  install_name_tool -change "$dependency" '@loader_path/libusb-1.0.dylib' "$native/dfu-util"
  otool -L "$native/dfu-util" | grep -F '@loader_path/libusb-1.0.dylib'
  # Sign before hashing. No later recursive signing may change these files.
  codesign --force --sign - --timestamp=none "$native/libusb-1.0.dylib"
  codesign --force --sign - --timestamp=none "$native/dfu-util"
  codesign --verify --strict "$native/libusb-1.0.dylib"
  codesign --verify --strict "$native/dfu-util"
fi
chmod 755 "$native/dfu-util"
"$native/dfu-util" --version
