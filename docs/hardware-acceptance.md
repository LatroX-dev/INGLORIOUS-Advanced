# Hardware acceptance

This is a developer and tester checklist, not a statement that the checks have passed.

## Record

Record model, ANSI/ISO layout, visible revision if available, operating system, USB connection, app version and explicitly reported firmware version. Keep original firmware and identifying diagnostics private.

Run the checklist separately on Windows, Linux and macOS. On Linux, include access before and after installing the supplied USB rules while running the app as a normal user. On macOS, include launching the packaged app and any system permission prompts. Check both direct ports and a USB hub: ambiguous or unavailable port information must stop the operation, never select a device by name alone.

## First installation and return

1. Save profiles and keep another keyboard available. Start with the Numpad when it is available.
2. Run original-firmware recovery through the app if a matching original backup exists. Stop if it does not.
3. Reconnect normally. Verify the guide detects the original device automatically. Check typing and lighting.
4. Run first installation. Confirm the displayed model. Verify the guide waits for a real disconnect and detects the correct update-mode device. Select **Back up and install** once; the guide must create and verify the original backup before writing, without another confirmation.
5. Complete installation, reconnect normally and verify that the guide advances automatically.
6. Test every physical key, knob direction and knob press. On the Numpad, test the slider with its configured function.
7. Test per-key colors, each side segment, synchronized sides and logo lighting where present. Check the full lighting sequence without pressing keys.
8. Transfer a profile with a harmless macro, another layer and standby enabled. Close the app, reconnect and verify the settings still work.
9. Restore original firmware again through the app. Confirm the displayed device and select **Back up and restore** once. The checks and restoration must run without another confirmation. Confirm normal recognition, typing and lighting. Record which original settings return and which do not.
10. Reinstall INGLORIOUS if desired, then load the saved profile.

## Update

Use a supported older INGLORIOUS version and a saved profile. Confirm the app displays the actual installed and included versions and offers only a newer version. Select **Back up and update** once; backup and update must run together and preserve the configuration. Close/reopen the app and reconnect the keyboard to check persistence. Test the current/equal/newer-version cases without writing: no pending update should be invented.

## Stop conditions

An ambiguous model, changed connection, missing matching archive, disagreement between reads, failed verification or uncertain write result stops the run. Retain evidence and investigate before trying again. Never deliberately unplug a device during a real firmware write for this checklist; interruption handling belongs in the simulated tests unless separate recovery equipment and a dedicated destructive test plan exist.

## Current status

The GMMK PRO revision 1 ISO and GMMK Numpad have historical physical-use evidence. On Windows, the Numpad completed original-firmware recovery and subsequent first installation with app 0.6.43. The combined backup/write flow, automatic reconnection after installation, full lighting sequence and typing test were confirmed by the tester.

After that restoration, Windows initially reported an unrecognized USB device; a second normal reconnection succeeded. The restoration had passed full readback verification. The cause of the USB enumeration failure remains undetermined. The subsequent installation completed with one normal reconnection after about five seconds unplugged.

The GMMK 3 PRO 100% ISO wired (non-HE) completed a separately authorized first installation and original-firmware restoration on one device. Both operations had two matching full readbacks; normal typing and lighting were confirmed after each reconnection. Its resident loader remained unchanged. This initial round trip used a local engineering build. Subsequent app installation and the update to firmware 0.11.4 with app 0.6.54 completed on Windows; the tester confirmed the full lighting sequence and return to normal lighting on 2026-10-16. This exact variant is no longer marked experimental.

This is partial acceptance: the full checklist above, including every control, configuration persistence and original restoration through the integrated app guide, remains open for that PRO variant. Thirteen other variants remain experimental and have no physical test evidence. Native macOS/Linux package, device, layout and permission testing is also pending. Automated checks and successful builds are reported separately.

## Physical controls

On 2026-10-18, the GMMK 3 PRO 100% ISO completed a local firmware 0.11.9 update with app 0.6.66 on Windows, followed by normal typing. The tester confirmed both OS-switch positions in the app, the actual left Windows/Alt exchange and return, operation after fully quitting the app, and switching while holding left Alt without a stuck modifier after release. See [hardware switches](hardware-controls.md) for the exact scope and remaining checks. This evidence does not establish physical acceptance for other models or for a macOS host.

For every applicable model, follow the [hardware-control checklist](hardware-controls.md). Verify both Windows/macOS switch positions, holding and releasing affected modifiers across a switch, unchanged custom bindings and all four layers, and operation with the app closed. A working key matrix does not validate rear switches. Before supporting a profile selector on another model, verify its electrical mapping, independent profile storage and update-preservation layout. Record automated preservation checks separately from a physical firmware-update test.

## Hardware profiles and app 0.7.0

The GMMK 3 PRO 100% ISO's three selector positions were independently mapped, including reverse switching and starting in another position. With firmware 0.11.11 on Windows, a tester confirmed separate saved lighting in each position, switching with the app fully closed, retained profiles after reconnecting, and app loading that follows the selected position. A temporary key assignment in profile 2 left the other profiles unchanged. The detailed scope is recorded in [hardware switches](hardware-controls.md).

With app 0.7.0 and the same firmware, the tester confirmed automatic configuration loading and one unchanged configuration transfer after the device-query corrections. This closes the requested single-transfer acceptance scenario, not a repeated-transfer or long-duration stress test.

Still open on physical hardware: all four layers, macros, knob settings and differing polling rates in each profile; preservation of three distinct profiles through a subsequent firmware update; and use on native macOS/Linux. Simulated update and recovery checks are separate evidence and must not be reported as a physical firmware-update test.
