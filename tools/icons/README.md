# Icons

`src/Fadenstack.UI.Avalonia/Themes/Icons.axaml` is generated: one `StreamGeometry` per icon, keyed `Icon.<Name>`, in the
24x24 design box of `@mui/icons-material` (the set the console uses). Draw them with the `FsIcon` control.

## Add an icon

1. Add its Material name, exactly as in `@mui/icons-material` (for example `Bookmark` or `BookmarkBorder`), to `icons.json`.
2. Run, with Node 20 and the package installed anywhere (the pinned version is checked):

   ```
   node tools/icons/build-icons.mjs <path>/node_modules/@mui/icons-material
   ```

3. Commit `icons.json` and the regenerated `Icons.axaml`. A test checks that every name in `icons.json` is in the file.

To move to a newer package version, change `version` in `icons.json` on purpose and review the diff of `Icons.axaml`.

## Fadenstack's own icons

An icon the Material set has no match for is drawn on the same 24x24 grid, with the same weight (filled shapes,
about 2-unit details), and listed under `own` in `icons.json`: its `name`, its `source` (where it comes from; the
generator writes it as a comment above the icon) and its path `data`. They come last in `Icons.axaml` and are
Apache-2.0, as the SDK.

- `Agent` (`Icon.Agent`) is the agent shown as itself: the stack avatar's silhouette, three plates seen from the
  front with the eyes cut out of the middle one (even-odd, `F0`) and its tail. Use it wherever the agent has an icon;
  `SmartToy` is a robot, not the agent. `agent-icon.py` derives it from the avatar's geometry
  (`python tools/icons/agent-icon.py` prints the path data for `icons.json`).
