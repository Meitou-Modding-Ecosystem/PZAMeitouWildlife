# The birds' rig

- **Skeleton and animations: [Mesh2Motion](https://mesh2motion.org)** (`static/assets/bird/model.glb`'s skeleton and
  `static/assets/bird/animations/*.glb` of [Mesh2Motion/mesh2motion-app](https://github.com/Mesh2Motion/mesh2motion-app)), released under
  **CC0 1.0** (public domain dedication). Thank you!
- **Fit and bake: PZA** (`mod/tools/birds/bake_bird_rig.py`): the skeleton scaled to the game bird's wingspan, its glide, flap, land,
  take-off, hop and idle clips baked as bone motion in `birdrig.pzab`. No game file is in here: the mod moves the game's own bird
  (read from your install) with this motion when the game starts.
