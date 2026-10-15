# Importing saved CORE settings

The optional importer reads `Devices.json` and `DeviceProfiles.json` from a selected CORE `datastore` folder. On Windows it first checks the current user's `Glorious Core/datastore` folder under roaming application data. It does not launch CORE, contact a device, save a profile or transfer settings.

The preview requires one matching CORE product instance, a unique current profile explicitly assigned to that instance, a unique profile record and a valid active layer from 0 through 2. Multiple matching devices, shared profile assignments, duplicate records/properties and malformed values are rejected. The exact CORE product mapping follows the stock catalog; a generic GMMK 3, an HE model, a wireless model or another layout cannot stand in for a GMMK 3 PRO 100% wired ISO.

Only selected values are applied to a detached copy of the current editor profile:

- Static per-key RGB, including CORE's 0–100 alpha premultiplication, and the stored global brightness from `presetLightingRecord.brightness`.
- Simple single-key `Keystroke` assignments with `modifier: None` and a verified physical key mapping. ISO `Hashtag` maps to `KC_NUHS`; ISO `Backslash`/`BackslashLeft` maps to `KC_NUBS`.

The active CORE layer becomes the copy's base layer. Other layers, macros, compound bindings, animated effects, side/badge lights and device settings are omitted. Unsupported or unselected values retain the current profile's values. Missing values are never filled with a guessed CORE setting. A new setup profile starts with the normal red defaults; an existing profile retains its stored colors wherever the import does not explicitly replace them.

The wired handler does not use `perKeyLightingRecord.brightness` for global hardware brightness, so the importer deliberately ignores that field. Cached per-key colors can remain in CORE even while an animated preset is selected. They are offered as **stored static colors**, not as a reconstruction of the keyboard's current appearance. The preview lists values and omission counts before opening an unsaved copy. Saving that copy and transferring it are separate user actions.

The evidence is the installed official CORE 2.1.21.4 source catalog identified in [the stock-device reference](../reference/glorious-stock-devices.json). Reviewed sections include RGBA alpha/RGB conversion (2056, 2181–2188), per-key serialization (2205–2289), profile/layer and instance association (2767–2988, 3297), exact PRO product/layout metadata (9438–9502), physical key and auxiliary-light maps (42181–42297, 43291), and wired RGB/brightness commands (46034–46098, 59929–59949). Proprietary source and personal cache records are not redistributed. Automated tests use synthetic cache data only; cache metadata does not prove a unique live hardware specimen.
