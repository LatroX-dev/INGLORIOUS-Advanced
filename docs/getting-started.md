# Set up your keyboard

## Install the app

Public downloads are not available yet. To build the source preview, see the [build instructions](public-release.md).

Choose the package for your computer when downloads become available:

- **Windows x64:** run **INGLORIOUS-Advanced-\<version\>-Setup.exe** and follow the installation guide. Open **INGLORIOUS Advanced** from the Start menu. No command script or separate .NET installation is needed.
- **macOS:** extract the archive and drag **INGLORIOUS Advanced.app** to Applications. Choose `osx-arm64` for Apple silicon or `osx-x64` for an Intel Mac. These builds are not Apple-notarized; macOS may ask you to approve the app in **Privacy & Security**.
- **Linux x64:** extract the archive and run **application/INGLORIOUS Advanced**. If USB access is blocked, follow the Linux USB instructions below.

Keep the application files together. First installation, firmware updates and restoration use the same guided steps on all three platforms. Native macOS/Linux hardware validation is still pending; see the [test status](hardware-acceptance.md#current-status).

### Open at system startup

The Windows installer selects **Open at system startup** by default. You can clear it during installation or change it later in the app's **Settings**. Updates preserve your choice. Autostart opens the app in the notification area rather than displaying its window. Use the icon to open or quit INGLORIOUS Advanced. Starting the app normally opens its window.

The same setting is available on macOS and Linux. macOS uses the menu bar; on Linux, a desktop without a supported tray shows the app window so it remains accessible.

If your operating system separately blocks login items, allow INGLORIOUS Advanced in its startup settings as well.

### Uninstall on Windows

Open Windows **Settings → Apps → Installed apps → INGLORIOUS Advanced → Uninstall**. Choose whether to keep your personal data or remove it with the app. Keeping data is selected by default.

Full removal also deletes the original-firmware backups in the app's data folder. Keep a separate copy if you want to return to the original firmware later. Files you exported to other folders are not removed. Uninstalling the app does not change the keyboard's firmware.

## Install INGLORIOUS firmware

1. Connect your keyboard by USB and open **Settings → Set up device**.
2. Choose the detected keyboard if more than one is connected. Check the full model name and layout. Experimental models are marked.
3. Follow the displayed instructions to disconnect it and reconnect with the shown keys held.
4. Confirm the detected model and select **Back up and install**. The app saves and checks the original firmware, then installs INGLORIOUS. If the backup cannot be verified, installation stops before anything is written to the keyboard.
5. Keep the app open and USB connected until it asks you to reconnect normally.
6. Select **Test lighting** for the approximately 21-second light sequence. Do not press keys during it. Then type **INGLORIOUS** in the test box, in any letter case, or **1234567890** on the Numpad. This finishes the guide; you can also select **Done**.

The app handles the file checks. You do not choose a firmware file or inspect checksums. If Glorious CORE is running, close it when prompted. If a driver or device cannot be verified, follow the displayed message; do not choose a similar model to continue.

First installation replaces the keyboard firmware and starts with new INGLORIOUS settings. The Numpad is **USB only — Bluetooth does not work** with INGLORIOUS firmware.

### Windows: if the USB driver is missing

Use the guide's **Set up USB driver (Zadig)** button only when prompted. In Zadig, select the exact keyboard device that the guide identified in update mode, choose **WinUSB** and install it. Never replace the normal keyboard or HID driver. If you cannot identify the update-mode device confidently, stop and leave the drivers unchanged. Return to the guide after installing the driver.

### Linux: allow USB access

If the app cannot access the keyboard, open a terminal in the extracted package folder and run:

```sh
sudo bash Install-UsbPermissions.sh
```

Confirm the prompt to install access rules for the supported USB devices. Then reconnect the keyboard and reopen the app. Run INGLORIOUS Advanced as your normal user, not as root.

## Make changes

At startup the app loads each connected INGLORIOUS device. Select **Lighting**, **Key assignments**, **Layers**, **Macros**, **Profiles** or **Device**, make your changes and use **Send to keyboard** at the bottom. After a successful transfer, the keyboard keeps working with the app closed.

Saving a profile keeps a copy on your computer. It does not change the keyboard until transferred. Macro and color libraries can be reused across compatible profiles; lighting templates show the matching device by default.

Use the **?** button next to Settings for help in your language. Language follows **System** by default.

## Update INGLORIOUS firmware

Every app package includes the matching firmware for all supported models. The app compares the version reported by the keyboard with this included firmware. An update is offered only when an older supported version is recognized. Installing a newer app does not flash the keyboard automatically. Open an available firmware update from the device's firmware area and follow the guided reconnect steps. Confirm the displayed device and select **Back up and update**. The backup, checks and update then run together.

An ordinary update preserves the stored INGLORIOUS configuration. Save a profile copy before updating. Do not disconnect the keyboard or close INGLORIOUS Advanced while the write is running. An unknown version is not treated as outdated.

After reconnecting, use **Test lighting** and the typing test described above, or select **Done**.

## Return to original firmware

1. Save any INGLORIOUS profiles you want to keep.
2. Open **Settings → Restore original firmware** and select the keyboard if asked.
3. Follow the reconnect instructions. Confirm the displayed device and select **Back up and restore**. The app checks the current firmware and matching original backup, then restores it without another confirmation.
4. Reconnect normally when asked, then test typing and lighting.

Restoring replaces INGLORIOUS and its stored configuration. It requires that keyboard's matching original backup. Keep a separate copy of the complete backup folder before replacing or reinstalling your system. The [backup locations](device-setup.md#backup-locations) are listed for each platform.

The backup covers internal firmware. External profile storage and wireless-module settings are not included, so original settings are not guaranteed to be restored completely. An interrupted firmware write can require separate recovery. If the app reports an uncertain result, leave the device connected and keep the diagnostic report; do not repeatedly retry.

## If a device is missing

- Use a USB data cable and wait for your system to finish detecting the keyboard.
- Close another configuration app if it is using the device.
- Check the [model list](model-support.md); similar product names may contain different hardware.
- On Windows, if asked for a bootloader driver, do not replace the normal keyboard driver. The firmware guide must identify the update-mode device first.
- On Linux, check USB access using the instructions above.
