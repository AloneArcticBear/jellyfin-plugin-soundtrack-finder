<!-- #claude -->
# Changelog

All versions target Jellyfin 12.4 (ABI `12.4.0.0`). Downloads (in [`releases_claude`](releases_claude) and in the Jellyfin catalog) are available for 1.0.0.0, 1.3.1.0 and 1.3.2.0; the source code in this repository is the current version.

## 1.3.2.0 – 2026-11-22
- "Now playing" pop-ups now reach every open Jellyfin Web tab or window of a browser. Jellyfin itself only sends messages to the most recently active connection, and all tabs of one browser share a session, so pop-ups often landed in the wrong tab.
- Pop-up timing uses only the player's own position reports. Jellyfin's per-second estimates were sometimes a second ahead.
- Clearer diagnostics when a browser hasn't re-reported pop-up support after a server restart; the status shows how many tabs are open.
- Builds from NuGet (`Jellyfin.Controller`/`Jellyfin.Model` 12.4) or offline from local assemblies.
- Plugin repository manifest for installing and updating through the Jellyfin catalog.

## 1.3.1.0 – 2026-11-22
- "Now playing" shows 🎵 in the text, because Jellyfin Web displays only the message text.
- Timing accurate to the second.
- Diagnostics: live status per device (title, app, position, pop-up support, current and next song), a "send test pop-up" button, and a log line for every pop-up.

## 1.3.0.0 – 2026-11-22
- "Now playing" pop-up while watching, switchable globally by admins and per user.
- Search across all titles by song or artist, plus statistics (top artists, songs used in the most titles).

## 1.2.3.0 – 2026-11-22
- Link "▶ anhören & bearbeiten" in every song list in the description opens the title on the Soundtracks page.
- Settings for the link and the public Jellyfin URL; `?item=<id>` deep links; page address with copy button in the dashboard.

## 1.2.2.0 – 2026-11-22
- Exclusion list for false matches (sound-effect libraries, cover factories), applied to already analyzed titles as well.
- Admin button to always exclude an artist. Manually edited songs are never removed.

## 1.2.1.0 – 2026-11-22
- Song list in the description is a proper Markdown list.
- Every timeline edit rewrites the description immediately.

## 1.2.0.0 – 2026-11-22
- Soundtracks page (`/SoundtrackFinder/App`) for all signed-in users, using their Jellyfin sign-in and library access.
- Users can edit and save songs; changes are recorded with their name; undo is protected against overwriting someone else's changes.

## 1.1.1.0 – 2026-11-22
- Playback streams until stopped; "follow" mode; dragging empty space scrolls; Space plays from the cursor.
- Saved songs appear in Jellyfin automatically (the music library is rescanned).

## 1.1.0.0 – 2026-11-22
- Timeline editor: move, resize, add, delete, rename, split and merge songs; music sections and Shazam samples shown; preview playback.
- Save songs to a Jellyfin music library as tagged MP3s with cover art.

## 1.0.0.0 – 2026-11-21
- First version: Shazam recognition in a nightly task, song list appended to the description, dashboard page with corrections, Apple Music link, scene as MP3.
