# branding/

The plugins' images, as their sources:

| Source | Shipped as | Where it shows |
| --- | --- | --- |
| `<Plugin>.svg` (1280x720) | `plugins/<Plugin>/image.png` | The plugin's card on the Dashboard's Plugins page. `Deploy-Plugin.ps1` copies it into the installed plugin folder and names it in `meta.json` (`"imagePath": "image.png"`), which Jellyfin serves at `GET /Plugins/{id}/{version}/Image`. |
| `AutoCollection.svg` (1000x1500) | `plugins/SmartSearch/Jellyfin.Plugin.SmartSearch/AutoCollections/AutoCollection.png` | Embedded in the Smart Search DLL: the poster an auto-collection's collection gets when it has none. |

- `gen.mjs` writes the SVGs next to itself. The texts, icons and colours are in it.
- `render.mjs` renders each SVG with headless Chrome or Edge (`--headless=new --window-size=W,H --screenshot`; `CHROME`
  picks the browser), re-encodes the PNG with `optimize-png.mjs` and copies it where it ships.
- `optimize-png.mjs` re-encodes 8-bit RGB and RGBA PNGs losslessly (per-row filters, deflate level 9, an opaque alpha
  channel dropped) with node's own zlib, and keeps a file when the result isn't smaller.

To change an image:

```powershell
node branding/gen.mjs
node branding/render.mjs
```

A render is the same picture, but not byte for byte: the browser's version and text smoothing change single pixels.
Commit the SVGs and the PNGs together.
