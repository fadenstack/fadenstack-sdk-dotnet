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
