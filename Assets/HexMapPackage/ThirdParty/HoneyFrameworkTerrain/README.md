# HoneyFramework terrain mixer resources

These compact grayscale resources are derived from the licensed local
HoneyFramework terrain sources at:

`D:\unity game hub\WW2_map_project\Assets\URP3D_HoneyFramework\HoneyFramework\Resources\Terrain\Ground`

Only HF's soft ownership masks are reused. The current project's generated
surface textures and procedural mountain height masks remain authoritative.
This keeps the useful overlapping-stamp behavior without restoring HF's old
per-chunk height, diffuse, and shadow render textures.

`HF Terrain Mixer Atlas.png` contains eight 256x256 horizontal panels:

1. Dirt / desert
2. Plains / grass
3. Dirt / open plains
4. Marsh / tundra
5. Plains / snow ground
6. Hill
7. Mountain
8. Neutral fallback

`HF River Mixer.png` is a compact copy of the meandering river ownership mask.
