# CInCot

Instant screenshots with redaction built in. Press **PrtSc**: the screen freezes and dims, you drag a
region, and the toolbar is already there. Blur, pixelate or black-box anything sensitive, then
**Enter** to copy. You don't have to open an editor or save first.

This is the Phase 1 MVP of the SwiftShot/CInCot design spec.

## Hotkeys

| Key | Action |
| --- | --- |
| PrtSc | Region capture (click a window to snap to it) |
| Ctrl + PrtSc | Full screen (monitor under the cursor) |
| Alt + PrtSc | Active window |
| Shift + PrtSc | Scroll capture: not built yet, falls back to region |
| Hold B / X / R while dragging | Start in Blur / Pixelate / Redact |
| Enter or Ctrl + C | Copy and close |
| Ctrl + S | Save to folder (and copy) and close |
| Ctrl + Z / Ctrl + Y | Undo / redo |
| Esc / right-click while selecting | Cancel |
| B X R U A T P H C | Blur, Pixelate, Redact, Rect, Arrow, Text, Pen, Highlighter, Crop |
| 1-6, [ ] | Colour, stroke width |
| Arrow keys (Shift = 10 px) | Nudge the selection |
| Shift while drawing | Square shapes, 45° arrows, straight strokes |

Running `CInCot.exe` a second time while it is in the tray starts a region capture, so you can
bind it to any launcher or mouse button too.

## Build

Requires the .NET 8 SDK on Windows 10/11.

```bash
dotnet build CInCot.sln -c Release
```

```bash
dotnet test CInCot.sln
```

```bash
dotnet publish src/CInCot/CInCot.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

## Layout

```text
src/CInCot/
  App/        tray app, settings window, icon
  Capture/    DXGI + GDI virtual-desktop capture, DIB sections, window and monitor discovery
  Core/       annotation document (undo/redo), annotations, redaction effects, settings
  Export/     flatten, clipboard, save
  Hotkeys/    low-level PrtSc hook on its own thread
  Native/     Win32 interop
  Overlay/    the frozen-screen overlay/editor and its toolbar
tests/CInCot.Tests/
```

## Notes

- Settings and logs live in `%LOCALAPPDATA%\CInCot`. Screenshots go to `Pictures\CInCot` by default.
- Redactions are flattened into the pixels on export. Blur and pixelate downsample and jitter the
  source, so the original text can't be recovered. Copies that contain redactions are kept out
  of Windows clipboard history.
- Windows 11 can also bind PrtSc to Snipping Tool. CInCot's hook gets the key first. To hand it back,
  use **Pause PrtSc hotkeys** in the tray menu. The tray also links to the Windows setting.
- Capture uses DXGI Desktop Duplication, kept warm on the GPU (via Vortice). It falls back to a GDI
  BitBlt for rotated monitors, the secure desktop, or any DXGI error. The overlay background is
  blitted from DIB sections, and annotations are drawn with GDI+.
- Measured on a 2-monitor desktop (2560×2520): the warm capture takes 15–17 ms, and the overlay is
  fully painted 61–72 ms after PrtSc. The first capture after launch takes ~106 ms. Set
  `CINCOT_TRACE=1` to log these timings to `%LOCALAPPDATA%\CInCot\logs`.
- Idle memory is ~75 MB private, mostly the Direct3D runtime that keeps capture fast.
- Not built yet (later phases of the spec): the Direct2D/DirectComposition renderer, OCR
  auto-redaction, scroll capture, history and cloud sharing.
