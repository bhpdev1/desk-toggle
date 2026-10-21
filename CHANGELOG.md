# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Microsoft Store package: `scripts/pack-msix.ps1` builds an unsigned MSIX
  (`packaging/AppxManifest.xml`, generated tile and taskbar assets, `resources.pri`).
- In the packaged build, *Démarrage automatique* drives the manifest's StartupTask
  (`StartupTaskAutostart`) instead of the HKCU Run key, whose writes are virtualized for
  packaged apps. The standalone exe keeps using the Run key.

## [2.3.0] - 2026-10-19

### Changed

- Unified visual identity ("Toggle Nuit") across the repository and executable:
  - Application icon (`app.ico`) rebuilt as a multi-resolution ICO (`16` to `256` px)
    featuring the dark navy toggle switch with the 4 desktop tiles on the knob.
  - Notification-area (tray) icon (`TrayIconArt`) redrawn with anti-aliased signed-distance
    rendering to mirror the toggle switch state (filled blue pill with right knob when
    icons are visible; outlined pill with left knob when icons are hidden).
  - Updated README hero (`assets/hero.png`), GitHub social preview (`assets/brand/social-preview.png`)
    and framed screenshots (`assets/screenshots/*.png`).

## [2.2.0] - 2026-10-10

### Changed

- Project renamed **DeskToggle** (brand, README and repository). The executable,
  namespace and autostart value keep the `DesktopIconsToggle` name for now.
- Relicensed from MIT to the **GNU General Public License v3.0**, matching FolderTags.
- New visual identity ("Lights Off"): logo, README hero, GitHub social preview and
  framed screenshots, rendered by `scripts/render-brand.ps1`.
- Single bilingual `README.md` (English / Français) replaces `README.fr.md`.

## [2.1.0] - 2026-08-26

### Added

- Tray menu icons, rasterised from [Lucide](https://lucide.dev), shown in
  colour with the enabled/disabled state of an option conveyed by colour
  (full colour when active, dimmed alpha when inactive) rather than by a
  checkmark alone.
- The hotkey capture dialog was rebuilt: it now uses the system message
  font (read via `SPI_GETNONCLIENTMETRICS` instead of a hard-coded font),
  scales correctly per-monitor DPI, follows the current Windows light/dark
  theme colours, draws rounded corners, and adds an explicit confirmation
  step before a new combination is committed.
- Toggle throttling: a toggle arriving less than 300 ms after the previous
  one is ignored, preventing flicker from a held key and preventing two
  toggles from reading the same stale state.
- The global hotkey registration is retried when the tray menu is opened,
  so a combination that failed to register at startup (for example because
  another application held it at the time) can recover without a restart.

### Changed

- None.

### Fixed

- None.

## [2.0.0] - 2026-08-25

### Added

- Configurable global hotkey (default `Ctrl+Alt+D`, with a fallback chain
  tried only until the user chooses a combination themselves), replacing
  the middle-double-click trigger.
- Taskbar auto-hide option, extending the toggle to the taskbar via
  Windows' own `ABM_SETSTATE` auto-hide mechanism.
- Settings persistence under `HKCU\Software\DesktopIconsToggle`.
- Crash-recovery markers: icons and taskbar state are repaired on the next
  launch if the previous process was killed instead of exited cleanly.

### Removed

- The middle-double-click trigger and its global mouse hook, which woke
  the process on every mouse event system-wide regardless of whether the
  desktop was in focus.

## [1.0.0] - 2026-08-25

### Added

- First version: a notification-area (tray) icon and a middle-double-click
  gesture to toggle desktop icons.
