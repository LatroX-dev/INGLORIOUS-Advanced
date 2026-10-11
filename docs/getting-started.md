# Set up your keyboard

## Install the app

Public downloads are not available yet. To build the source preview, see the [build instructions](public-release.md).

Once a Windows x64 ZIP is available in Releases, extract it and run **Install.cmd**. Open **INGLORIOUS Advanced** from the Start menu. Keep all files together if using the portable executable instead.

## Install INGLORIOUS firmware

1. Connect your keyboard by USB and open **Settings → Set up device**.
2. Choose the detected keyboard if more than one is connected. Check the full model name and layout. Experimental models are marked.
3. Follow the displayed instructions to disconnect it and reconnect with the shown keys held.
4. Wait while the app saves the original firmware. Confirm installation when ready.
5. Keep the app open and USB connected until it asks you to reconnect normally.
6. Select **Test lighting** for the approximately 21-second light sequence. Do not press keys during it. Then type **INGLORIOUS** in the test box, in any letter case, or **1234567890** on the Numpad. This finishes the guide; you can also select **Done**.

The app handles the file checks. You do not choose a firmware file or inspect checksums. If Glorious CORE is running, close it when prompted. If a driver or device cannot be verified, follow the displayed message; do not choose a similar model to continue.

First installation replaces the keyboard firmware and starts with new INGLORIOUS settings. The Numpad works through USB only.

### If the USB driver is missing

Use the guide's **Set up USB driver (Zadig)** button only when prompted. In Zadig, select the exact keyboard device that the guide identified in update mode, choose **WinUSB** and install it. Never replace the normal keyboard or HID driver. If you cannot identify the update-mode device confidently, stop and leave the drivers unchanged. Return to the guide after installing the driver.

## Make changes

At startup the app loads each connected INGLORIOUS device. Select **Lighting**, **Key assignments**, **Layers**, **Macros**, **Profiles** or **Device**, make your changes and use **Send to keyboard** at the bottom. After a successful transfer, the keyboard keeps working with the app closed.

Saving a profile keeps a copy on your computer. It does not change the keyboard until transferred. Macro and color libraries can be reused across compatible profiles; lighting templates show the matching device by default.

Use the **?** button next to Settings for help in your language. Language follows **System** by default.

## Update INGLORIOUS firmware

The app compares the version reported by the keyboard with the firmware included in the app. An update is offered only when an older supported version is recognized. Open it from the device's firmware area and follow the guided reconnect steps.

An ordinary update preserves the stored INGLORIOUS configuration. Save a profile copy before updating. Do not disconnect the keyboard or close INGLORIOUS Advanced while the write is running. An unknown version is not treated as outdated.

After reconnecting, use **Test lighting** and the typing test described above, or select **Done**.

## Return to original firmware

1. Save any INGLORIOUS profiles you want to keep.
2. Open **Settings → Restore original firmware** and select the keyboard if asked.
3. Follow the reconnect instructions and confirm restoration.
4. Reconnect normally when asked, then test typing and lighting.

Restoring replaces INGLORIOUS and its stored configuration. It requires that keyboard's matching original backup. Backups are in `%LOCALAPPDATA%\INGLORIOUS Advanced\Firmware\Devices`; keep a separate copy before replacing or reinstalling Windows.

The backup covers internal firmware. External profile storage and wireless-module settings are not included, so original settings are not guaranteed to be restored completely. An interrupted firmware write can require separate recovery. If the app reports an uncertain result, leave the device connected and keep the diagnostic report; do not repeatedly retry.

## If a device is missing

- Use a USB data cable and wait for Windows to finish detecting the keyboard.
- Close another configuration app if it is using the device.
- Check the [model list](model-support.md); similar product names may contain different hardware.
- If asked for a bootloader driver, do not replace the normal keyboard driver. The firmware guide must identify the update-mode device first.

The macOS and Linux previews do not install or restore firmware.
