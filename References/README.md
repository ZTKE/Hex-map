# Reference projects

## WW2_map_project / HoneyFramework

`WW2_map_project` is the complete Unity project used as the local
HoneyFramework implementation reference. It is included as a Git submodule
from:

`https://github.com/ZTKE/WW2_map_project.git`

The HoneyFramework package is located at:

`WW2_map_project/Assets/URP3D_HoneyFramework/HoneyFramework`

Use this copy when checking the original terrain baking, mixer, height,
foreground, river, water, data-model, and editor behavior. The reference lives
outside this project's `Assets` and `Packages` directories intentionally, so
Unity does not import or compile the two projects together.

After cloning `hex-map`, initialize the reference with:

`git submodule update --init --recursive`

Runtime code for `hex-map` remains under `Assets`; changes made inside the
reference submodule belong to the separate `WW2_map_project` repository.
