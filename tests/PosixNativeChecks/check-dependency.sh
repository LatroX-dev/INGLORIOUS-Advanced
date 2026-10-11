#!/usr/bin/env bash
set -euo pipefail

# Run the actual extraction and its guard, without building or opening USB devices.
# On macOS this deliberately uses the system BSD awk, as the native build does.
source_file=${1:-"$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)/scripts/build-posix-native.sh"}
extract=''
guard=''
matches=0
while IFS= read -r line || [[ -n "$line" ]]; do
  line=${line%$'\r'}
  if [[ "$line" == *'dependency=$(otool '* ]]; then
    extract=$line
    matches=$((matches + 1))
    IFS= read -r guard
    guard=${guard%$'\r'}
  fi
done < "$source_file"
[[ "$matches" -eq 1 && "$guard" == *'[[ "$dependency" '* ]] || {
  printf '%s\n' 'FAIL: Could not locate the production dependency extraction and guard.' >&2
  exit 1
}

prefix='/build/prefix'
native='/build/native'
fixture=''
checks=0
otool() {
  [[ "$#" -eq 2 && "$1" == '-L' && "$2" == "$native/dfu-util" ]] || return 91
  printf '%s\n' "$fixture"
}
extract_dependency() (
  # Only these two reviewed source lines are evaluated. otool is the fixture above.
  eval "$extract" || return "$?"
  eval "$guard" || return "$?"
  printf '%s' "$dependency"
)
accept() {
  local label=$1 expected=$2 actual
  fixture=$3
  actual=$(extract_dependency) || {
    printf 'FAIL: %s was rejected.\n' "$label" >&2
    exit 1
  }
  [[ "$actual" == "$expected" ]] || {
    printf 'FAIL: %s selected an unexpected dependency.\n' "$label" >&2
    exit 1
  }
  checks=$((checks + 1))
}
reject() {
  local label=$1
  fixture=$2
  if extract_dependency >/dev/null; then
    printf 'FAIL: %s was accepted.\n' "$label" >&2
    exit 1
  fi
  checks=$((checks + 1))
}

versioned='/build/prefix/lib/libusb-1.0.0.dylib'
unversioned='/build/prefix/lib/libusb-1.0.dylib'
metadata='(compatibility version 5.0.0, current version 5.0.0)'
accept 'Versioned libusb' "$versioned" "$native/dfu-util:
    $versioned $metadata
    /usr/lib/libSystem.B.dylib (compatibility version 1.0.0, current version 1345.120.2)"
accept 'Unversioned libusb' "$unversioned" "    $unversioned $metadata"
accept 'Multiple version components' '/build/prefix/lib/libusb-1.0.0.5.dylib' "    /build/prefix/lib/libusb-1.0.0.5.dylib $metadata"
accept 'Tabs and leading whitespace' "$versioned" $'\t'"$versioned"$'\t'"$metadata"
accept 'Ignore misleading lines around the dependency' "$versioned" "/build/prefix/lib/libusb-1.0.dylib:
    /build/prefix/lib/libusb-1.0/other.dylib $metadata
    /build/prefix/lib/libusb-1.0.dylib.backup $metadata
    /build/prefix/lib/not-libusb-1.0.dylib $metadata
    /build/prefix/lib/libusb-1x0.dylib $metadata
    $versioned $metadata
    /usr/lib/libSystem.B.dylib $metadata"

reject 'Missing libusb' "$native/dfu-util:
    /usr/lib/libSystem.B.dylib $metadata"
reject 'Empty output' ''
reject 'A binary header is not a dependency' '/build/prefix/lib/libusb-1.0.dylib:'
reject 'Slash inside the library suffix' "    /build/prefix/lib/libusb-1.0/other.dylib $metadata"
reject 'Wrong library name' "    /build/prefix/lib/not-libusb-1.0.dylib $metadata"
reject 'Literal dot in the library name' "    /build/prefix/lib/libusb-1x0.dylib $metadata"
reject 'Backup file suffix' "    /build/prefix/lib/libusb-1.0.dylib.backup $metadata"
reject 'Dependency outside the build prefix' "    /usr/local/lib/libusb-1.0.0.dylib $metadata"
reject 'Similar build prefix' "    /build/prefix/lib-other/libusb-1.0.0.dylib $metadata"
reject 'Relative dependency' "    @rpath/libusb-1.0.0.dylib $metadata"
reject 'Two matching libraries' "    $versioned $metadata
    $unversioned $metadata"
reject 'Duplicate dependency lines' "    $versioned $metadata
    $versioned $metadata"
reject 'A second matching library outside the build prefix' "    $versioned $metadata
    /usr/local/lib/libusb-1.0.dylib $metadata"

printf 'PASS: %s native dependency extraction checks; no hardware access.\n' "$checks"
