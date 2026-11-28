# The Mire Stilt

- **Models and textures: PZA** (made with Meshy on PZA's own plan, so they are PZA's): the flight model (wings spread) and the standing
  model made from its look (the membrane wings folded), each cut down to about 6,000 triangles for the game.
- **Skeleton and animations: [Mesh2Motion](https://mesh2motion.org)** (the bird's skeleton and its take-off, flap, glide, land, idle and
  walk clips from [Mesh2Motion/mesh2motion-app](https://github.com/Mesh2Motion/mesh2motion-app)), released under **CC0 1.0** (public
  domain dedication). Thank you!
- **Rig, wade, hunt and bake: PZA** (`mod/tools/stilt/stilt_rig.py`, `bake_stilt.py`): three neck bones added to the bird, the slowed
  high-stepping wade, the hunt (stalk, coil, strike, shake, swallow), baked as frames in `stilt_ground.pzaf` and `stilt_air.pzaf`.
