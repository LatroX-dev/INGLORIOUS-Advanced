# Set up your keyboard

## Install the app

Choose the package for your computer from the [Releases page](https://github.com/LatroX-dev/INGLORIOUS-Advanced/releases). To build from source, see the [build instructions](public-release.md).

- **Windows x64:** run **INGLORIOUS-Advanced-\<version\>-win-x64-Setup.exe** and follow the installation guide. Open **INGLORIOUS Advanced** from the Start menu. No command script or separate .NET installation is needed.
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

### Update the app

Open **Settings → Updates → Check for updates**. Automatic checks are enabled by default and can be turned off there. Select **Download update** to see the new version's changes and download its package.

On Windows, select **Install now** when the download is ready. Save any changes when prompted; the app closes and the normal installer opens. On macOS or Linux, open the download folder and follow the displayed replacement instructions. Profiles and original-firmware backups remain in the app's separate data folder.

The app verifies the downloaded package automatically. If verification or downloading fails, try again; an incomplete download is never installed. App updates include new firmware when available, but do not install it on your keyboard automatically.

## Install INGLORIOUS firmware

1. Connect your keyboard by USB and open **Settings → Devices & data → Set up device**.
2. Choose the detected keyboard if more than one is connected. Check the full model name and layout. Experimental models are marked.
3. Follow the displayed instructions to disconnect it and reconnect with the shown keys held.
4. Confirm the detected model and select **Back up and install**. The app saves and checks the original firmware, then installs INGLORIOUS. If the backup cannot be verified, installation stops before anything is written to the keyboard.
5. Keep the app open and USB connected until it asks you to reconnect normally.
6. Select **Test lighting** for the approximately 21-second light sequence. Do not press keys during it. Then type **INGLORIOUS**, in any letter case, or **1234567890** on the Numpad while the guide is open. This finishes the guide; you can also select **Done**. **Test device** shows the keyboard layout so you can check individual keys.

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

In **Lighting**, use the **Colors** and **Effects** tabs to choose what you edit. The palette icon opens lighting templates. Switching **Global effect** off shows the stored colors on LEDs without their own effect; black stays unlit. Individual LED effects remain unchanged. Apply your edits, then select **Send to keyboard**.

Under **Device**, configure response time and lighting during inactivity. Firmware and backups are on the same page.

Saving a profile keeps a copy on your computer. It does not change the keyboard until transferred. Macro and color libraries can be reused across compatible profiles; lighting templates show the matching device by default.

Use the **?** button next to Settings for help in your language. Language follows **System** by default.

## Update INGLORIOUS firmware

Every app package includes the matching firmware for all supported models. The app compares the version reported by the keyboard with this included firmware. An update is offered only when an older supported version is recognized. Installing a newer app does not flash the keyboard automatically. Open an available firmware update from the device's firmware area and follow the guided reconnect steps. Confirm the displayed device and select **Back up and update**. The backup, checks and update then run together.

An ordinary update preserves the stored INGLORIOUS configuration. Save a profile copy before updating. Do not disconnect the keyboard or close INGLORIOUS Advanced while the write is running. An unknown version is not treated as outdated.

After reconnecting, use **Test lighting** and the typing test described above, or select **Done**.

**Test device** shows the layout of every supported keyboard, including the Numpad. Green keys are confirmed by the keyboard's diagnostic mode. If that mode is unavailable, yellow keys show matching inputs received by the computer; custom bindings or another connected keyboard can produce different positions. For Numpad digits, turn on Num Lock (the standard shortcut is **Fn + 0**).

Experimental models show one optional feedback invitation after a successful installation or configuration transfer. You can choose **Later** and return through **Device → Feedback & contact**. Problem reports include the exact model, layout, app and firmware versions, and system information. Add the steps, expected behavior and actual behavior, then review the complete text before opening GitHub or email. Profiles, firmware backups, serial numbers and local paths are not collected. Long reports can be copied without truncation.

## Choose a knob function

Select the knob or a rotation arrow under **Key bindings**. In app 0.6.61 or newer, **Presets** shows the ready-made knob functions; **Manual** lets you assign turning left, turning right and pressing separately. Switching views does not change your bindings. **Short press → Edit** opens the press assignment directly.

The presets offer volume, brightness, tracks, vertical and horizontal scrolling, zoom, switching tabs, and undo/redo. For zoom and undo/redo, choose the preset for Windows/Linux or macOS. These shortcuts and switching tabs depend on the active application. A preset changes both rotation directions on the selected layer and preserves the short-press action. Separately assigned directions appear as **Custom bindings**. Choose **Transfer to keyboard** to save the changes on the device.

**Hold to switch function** is optional and off by default. It requires firmware 0.11.6 or newer, as do the new scrolling, zoom, tab and undo/redo actions. Under **Order**, select the functions you want in the cycle and the operating system for their shortcuts. Hold the knob for 700 ms to move to the next selected function without the app running. If you have custom rotation bindings, they are also included in the cycle. A short press still uses its assigned action. This option is unavailable when the short press is assigned to Fn, a layer or a key combination. Reconnecting the keyboard or transferring a profile returns to the saved knob function.

In **Order**, use **Add function** or **Remove** to choose the cycle. The up and down arrows set its order. A custom order requires firmware 0.11.7 or newer and stays saved even while long-press switching is disabled. Transfer the changes to use that order with the app closed.

**Steps per notch** adjusts the volume, brightness, scrolling and zoom presets from 1 to 5 steps per turn of the knob. Track changes, tab changes and undo/redo remain one action per notch, as do custom bindings. The selected step size is saved on the keyboard and also requires firmware 0.11.6 or newer.

For undo/redo, set **Keyboard layout for undo** to match the computer's active keyboard layout: QWERTY, QWERTZ or AZERTY. This controls the shortcut; it does not change your operating system's keyboard layout. Shortcut support can differ between applications.

With **Brightness**, turning left dims the lighting and turning right brightens it. Brightness bindings require firmware 0.11.5 or newer. Individual keys can also use **Lighting brighter** or **Lighting dimmer**.

The knob dims keys and side lighting together, including individual effects, with the app closed. Its binding stays saved on the device. The temporary dimming level resets when the keyboard loses power or a profile is transferred. On the Numpad, moving a slider assigned to brightness sets the level to the slider's position again.

For a saved brightness setting on specific keys or side LEDs, select them under **Lighting → Effects → Selection** and adjust **Brightness**. This also works with static lighting. Global settings apply to LEDs without individual effect settings.

## Return to original firmware

1. Save any INGLORIOUS profiles you want to keep.
2. Open **Settings → Devices & data → Restore original firmware** and select the keyboard if asked.
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
