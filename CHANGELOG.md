# Changelog

All notable changes to SoulsTracker are documented here.

## 1.2.1 - 2026-07-29

* Fixed the Skull-only Total Deaths icon so it remains visible when the title is blank.
* Made the Total Deaths browser overlay anchor inside the OBS Browser Source canvas for reliable left, center, and right placement.
* Kept automatic memory-based death totals visible through brief game loading transitions.
* Restored Elden Ring boss display names to their canonical forms.

## 1.2.0 - 2026-07-26

* Added automatic, read-only Elden Ring save discovery, character selection, and lifetime death totals.
* Added automatic, read-only Black Myth: Wukong Steam and Epic save discovery, slot selection, lifetime death totals, and useful slot details.
* Added a Black Myth: Wukong boss checklist.
* Added boss-list filters that adapt to each game's available base-game and DLC content.
* Added boss search, including normal reset behavior when changing games or restarting the app.
* Stabilized the Boss List browser overlay as a fixed 600 x 1080 widget so long boss names no longer move the list during a stream.
* Clarified empty boss lists and missing death totals; valid save readers now show zero until a death total is available.
* Streamlined game and save selection, filtered empty Elden Ring character slots, and fixed the restored game name sometimes appearing blank at startup.
* Death-sound volume changes now save automatically.
* Improved shutdown reliability and added a packaged-app shutdown benchmark.

## 1.0.3 - 2026-07-22

* Added a Play button for death sounds so you can test the sound and volume before going live.
* Cleaned up the Death Sound settings. Buttons now only work when they should, volume is easier to edit, and clearing a sound no longer disables it.
* Fixed the same clear and enable issue with the Deaths and Boss List TXT exports.
* Cleaned up the Overlay tab with better spacing, simpler settings, an optional background toggle, and the normal Windows color picker.
* Added a quick explanation for adding the overlays to OBS or other streaming software.
* Fixed weird purple focus and highlight behavior on tabs, dropdowns, and inputs.
* Fixed some layout issues where settings or content could get cut off.
* Improved app closing speed and reliability.
* Reordered the games by release date while keeping the Dark Souls trilogy together.
* Renamed Demon's Souls to Demon Souls.

Elden Ring is still coming soon and is not supported in this update.

## 1.0.0 - 2026-07-17

Initial public release.

- Windows desktop death tracker and boss-list companion for supported Soulsborne games.
- Local OBS Total Deaths and Boss List browser overlays.
- Independent manual counters for Bloodborne and Demon Souls.
- Local-only settings, custom overlay presentation, text exports, and configurable hotkeys.
