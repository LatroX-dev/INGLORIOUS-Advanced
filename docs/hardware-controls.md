# Hardware switches

App 0.7.0 includes firmware 0.11.11 for the GMMK 3 PRO 100% ISO and 0.11.9 for the other supported models. The Windows/macOS switch is supported on the GMMK 3 wired models listed below; three independent hardware profiles are supported only on the GMMK 3 PRO 100% ISO. Both functions work with the app closed. The PRO has passed the Windows-side checks described below; physical verification on the other models is still pending.

The switch adapts unchanged standard modifier keys to the selected operating system. It preserves all four user layers and does not select a different profile. Custom bindings, key combinations, macros and knob actions keep the settings chosen in the app. A custom assignment with exactly the same keycode as the original standard binding follows that standard binding's conversion.

After moving the switch, release all keys so the new setting can take effect. This avoids changing the meaning of a key while it is held.

## Test the switch

Open **Device → Test device**, then choose **Start test**. The Windows/macOS status shows the mode reported by the keyboard. Move the physical switch to each position and check that the displayed mode follows it. When keys are held, the display distinguishes the active mode from the pending selection.

The main editor shows switch icons and the reported positions between the model and layer selection. Hover over an icon to identify its physical switch. The display does not change the keyboard setting. Its low-frequency reads share the foreground-operation pause with the knob monitor; transfers and tests wait for the current read to finish. Older firmware keeps the ordinary typing test available.

The status appears at the top right beside the device name. The keyboard diagram identifies physical key positions; its labels do not exchange when the OS switch moves. Close the device test before checking the actual modifier actions in another application.

On 2026-10-18, a tester confirmed the following on Windows with the GMMK 3 PRO 100% ISO, local app 0.6.66 and firmware 0.11.9: status follows both switch positions; left Alt and left Windows exchange functions in macOS mode and return in Windows mode; switching still works after quitting the app; and holding left Alt while switching to macOS, then releasing it, leaves normal typing and the new modifier action working. Right-side modifiers, custom bindings across all four layers, and use on an actual macOS host remain separate checks.

## Model coverage

| Keyboard | Physical controls covered by the inventory | INGLORIOUS support |
| --- | --- | --- |
| GMMK PRO Rev1 and Rev2, ANSI and ISO | No OS/profile/connection selector defined in the pinned board sources | No switch status or switch commands are offered |
| GMMK 2 65% and 96%, ANSI and ISO | No OS/profile/connection selector defined in the pinned board sources | No switch status or switch commands are offered |
| GMMK Numpad | Connection switch; front brightness/volume slider | USB only; Bluetooth is not supported. The front slider is configured separately under key bindings |
| GMMK 3 65%, ANSI and ISO, wired non-HE | Windows/macOS switch; three-position profile switch | OS switch in firmware 0.11.9; profile switch not supported |
| GMMK 3 75%, ANSI and ISO, wired non-HE | Windows/macOS switch; three-position profile switch | OS switch in firmware 0.11.9; profile switch not supported |
| GMMK 3 100%, ANSI and ISO, wired non-HE | Windows/macOS switch; three-position profile switch | OS switch in firmware 0.11.9; profile switch not supported |
| GMMK 3 PRO 100% ISO, wired non-HE | Windows/macOS switch; three-position profile switch | OS switch since firmware 0.11.9; three separate onboard profiles in 0.11.11, with the hardware checks recorded below |

These are explicit model identities, not a rule based on the product name. Other GMMK 3 PRO sizes/layouts and HE or wireless variants do not inherit this support. See [model support](model-support.md).

## Implementation and evidence

The reviewed Glorious GMMK 3 sources define the OS input as A13 for 65%, 75% and 100% boards. The input interpretation follows each variant's original callback, including the different polarity in the 75% ANSI source. The PRO 100% ISO target uses the separately identified derivative of the 100% ISO port; its OS-switch input and left-side modifier behavior have the physical evidence recorded above.

The manufacturer keymaps implement the OS switch by selecting their Windows or macOS base layer. INGLORIOUS instead retains its four independently configured layers. Its conversion is limited to unchanged plain stock bindings at their original physical positions: left GUI and left Alt exchange roles, right Alt becomes right GUI, and the 100% board's application/menu key becomes right Alt. A transparent assignment keeps normal layer fallback. The firmware does not rewrite saved profiles when the switch moves.

The profile selector is a separate feature. On the GMMK 3 PRO 100% ISO, controlled observations established positions 1/2/3 at matrix rows 11/12/13, column 7, including reverse switching and reconnecting in position 3. This mapping is not applied to any other model. Firmware 0.11.11 gives each position a full configuration with four layers. It reads the previous configuration as the initial fallback and never writes flash merely because a switch moved.

On 2026-10-19, a tester confirmed independent profile saves on Windows with the GMMK 3 PRO 100% ISO, wired non-HE, private app 0.6.68 and firmware 0.11.11. Saving a static magenta Esc key in profile 1 left profiles 2 and 3 unchanged. Blue and red Esc keys were then saved independently in profiles 2 and 3. With the app fully exited, switching 1 → 2 → 3 → 1 restored magenta, blue, red and magenta respectively, with normal typing. After selecting profile 2, disconnecting USB for five seconds and reconnecting normally with the app still closed, the keyboard started with blue; subsequent switching through all three positions restored their respective colors and normal typing.

The tester also selected profile 2 while the app was closed and confirmed that opening the app loaded profile 2 with its blue Esc key. Subsequent switching to profiles 1 and 3 updated the preview to magenta and red without a manual read or transfer.

A temporary F12-to-B assignment on layer 0 of profile 2 also worked, while profiles 1 and 3 retained their previous assignments in the app. Restoring the original binding succeeded on a second attempt. The first attempt stopped at device discovery before any upload; this intermittent discovery failure is tracked separately from profile isolation.

On 2026-10-19, after installing app 0.7.0 while keeping firmware 0.11.11, the tester confirmed automatic loading of profile 2 and one unchanged configuration transfer. The requested scenario included first-attempt success, automatic dialog closure, and retained lighting and typing. This is one combined user confirmation, not a repeated-transfer or long-term reliability result.

These checks establish separate saved lighting changes, switching without the app, persistence across a restart in profile 2, and loading the selected profile at app startup and during switching. They do not yet verify all four layers, macros, knob settings, different polling rates per profile, or firmware update or restore preservation. Those remain separate hardware acceptance checks.

In the Windows editor, switching profiles loads the selected configuration and retains each slot's unsaved draft. An unfinished input remains visible until it is completed. macOS and Linux keep the editor bound to the explicitly loaded slot; use **Read from keyboard** to load the newly selected profile. Transfers on all platforms verify that the intended profile is selected and never change their destination during an upload.

Each hardware profile also stores its polling rate. When a different rate is needed after connecting the keyboard or moving the profile switch, the firmware briefly reconnects USB after an idle delay. It waits for keys, macros, transfers and device tests to finish, and does not interrupt an active configuration read. Saving a profile lets the app verify the saved configuration before requesting that reconnect. Actual USB behavior on each operating system remains a separate hardware acceptance check.

Source references:

- [Manufacturer source catalog](../reference/glorious-gmmk3/sources.json), including the original per-variant `config.h` and default keymaps.
- [Firmware targets](../firmware/targets.json) and [GMMK 3 PRO port](gmmk3-pro-port.md).
- [Explicit app control inventory](../src/InGlorious.Core/HardwareControlCatalog.cs).
- [Firmware protocol](../firmware/protocol.md).

The app's read-only switch status is separate from physical key/LED diagnostics. Generic GMMK 3 boards can therefore show the OS switch while retaining the ordinary typing test, even when full physical diagnostics are unavailable.
