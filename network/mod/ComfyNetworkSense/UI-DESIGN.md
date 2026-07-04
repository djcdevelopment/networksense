# NetworkSense UI Design

## Direction

ComfyNetworkSense is now HUD-first. The upper-left overlay is the normal play
surface: compact, wide, low-scroll, and readable while moving through the world.
The panel is a debug drawer for deliberate actions: manual checks, benchmarks,
exports, config reload/profile application, raw signals, and Raven requests.

The previous parchment/brown mock remains useful as a feature inventory, but the
in-game implementation uses a modern Nordic Glass theme: dark blue-black glass,
cool cyan accents, muted slate text, and green/amber/rust state colors.

## Surfaces

- `HUD`: primary awareness surface. `Compact` is two wide lines, `Minimal` is one
  line, and `Diagnostic` starts at the four-line diagnostic view.
- `Debug`: practical controls and live read. This replaced the old `Ward` tab;
  `ward` remains a command alias for compatibility.
- `Signals`: raw telemetry and score snapshots for troubleshooting.
- `Raven`: async local-agent recap, next-test guidance, config suggestion,
  markers, notes, and recent event context.

## Commands

```text
network_sense_hud
network_sense_detail
network_sense_panel
network_sense_panel debug
network_sense_panel signals
network_sense_panel raven
network_sense_debug
network_sense_raven
```

## Config

HUD tuning lives in the BepInEx config:

- `hudPreset`: `Minimal`, `Compact`, or `Diagnostic`.
- `hudOpacity`: compact HUD background opacity.
- `hudMaxWidth`: wide HUD width before scale is applied.
- `hudScale` and `hudMarginPixels`: existing placement controls.

## Interaction Rules

- All MCP/Raven calls are explicit button presses.
- MCP calls are asynchronous and never block the Unity main thread.
- Raven/model output is advisory text only.
- JSONL telemetry remains the source of truth.
- Panel input releases the cursor and suppresses local player input while open.
- Esc closes the panel.

## Mock Reference

Design mock files:

- `network/design_mocks/Network Ward.dc.html`
- `network/design_mocks/.thumbnail`

The mock informed the feature set, not the final theme.
