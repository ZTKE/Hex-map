# WW2 sphere migration source

This folder contains the minimum source bundle needed to migrate the edited
sphere map into the flat hex map.

Source precedence:

1. `country_settings_5.tsv` is the primary country ID/name table.
2. `Country.csv` only fills IDs missing from the primary table and can later
   provide gameplay metadata.
3. `vert_buf_data_5.bytes` is authoritative for final per-tile ownership and
   actual country colors.
4. `CurrentCountryBlocks_R5_4096x2048.png` is the lossless, point-sampled flat
   ownership raster generated from the final sphere data.
5. `CityBrushCities.json` preserves all source city positions. During import,
   each city's final country must be refreshed from `vert_buf_data_5.bytes`
   using its `tileId`.

The bundled flat default map samples approximately 15%-88.89% of the exported
raster, or 63°S through 70°N. It omits Antarctica, places the northern edge just
above Iceland, and does not stretch the remaining landmass.

The bake resolves every flat land cell's source color through the authoritative
`vert_buf_data_5.bytes` ID/color pairs and stores a `ushort countryId` directly
on the cell. Map format version 12 saves both these IDs, their compact color
palette, and the sparse city list. City unit vectors use the exact same
equirectangular projection and latitude crop as the country raster, then bind to
the nearest matching land hex. The high-altitude political view is rebuilt from
cell ownership and draws borders along real shared hex edges; it no longer uses
a precolored PNG overlay. Manually creating a new map starts with country ID zero
and no cities.

Do not use the old `EarthTerritories_ww2.png` as migration authority.

Known source audit notes:

- The final raster contains 101 unique ownership colors.
- The city database contains 623 valid records.
- 70 city records have a brush-time `belongId` that differs from final sphere
  ownership; the importer must preserve the position and update the owner.
- IDs 19 and 54 are used by the sphere buffer but are absent from both country
  tables. They must remain explicit unresolved entries until their intended
  names/ownership are confirmed.
