#!/usr/bin/env bash
set -euo pipefail
if [[ $(uname -s) != Linux ]]; then printf '%s\n' 'This step is only needed on Linux.'; exit 1; fi
if [[ $EUID != 0 ]]; then printf '%s\n' 'Run sudo bash Install-UsbPermissions.sh to install USB access rules. Do not run the application with sudo.'; exit 1; fi
folder=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)
rules="$folder/70-inglorious.rules"
[[ -f "$rules" && ! -L "$rules" ]] || { printf '%s\n' 'USB rule file is missing.'; exit 1; }
printf '%s\n' 'Allow the active local user to access supported keyboard USB interfaces and the STM32/WB32 update interfaces?' 'This installs /etc/udev/rules.d/70-inglorious.rules. No firmware is changed.'
read -r -p 'Type yes to continue: ' answer
[[ "$answer" == yes ]] || exit 1
install -m 0644 -- "$rules" /etc/udev/rules.d/70-inglorious.rules
udevadm control --reload-rules
printf '%s\n' 'USB rules installed. Disconnect and reconnect your keyboard once, then open INGLORIOUS Advanced normally.'
