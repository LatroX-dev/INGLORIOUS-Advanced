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

The GMMK PRO revision 1 ISO and GMMK Numpad have historical physical-use evidence. On Windows, the Numpad lighting sequence and typing test passed after first installation with app 0.6.41. The unified installation, update and restoration workflow in app 0.6.43 still needs the above acceptance run. The other 13 variants remain experimental. Native macOS/Linux package, device, layout and permission testing is also pending. Automated checks and successful builds are reported separately.
