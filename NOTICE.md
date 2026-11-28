# PZA's Wildlife for Meitou: credits and notices

**Author:** PZA. **Licence:** GNU General Public License version 3 or later (`LICENSE`).
Unofficial fan-made mod; not affiliated with Lo-Fi Games or the Meitou project.

- **MeitouClient** (GPL-3.0-or-later): the game this code mod runs in. Its assemblies and Harmony (`0Harmony`, MIT) come with the game and
  are referenced, never copied here. Facts about its bird code were read from its source and `docs/game/birds.md`; no code was copied.
- **The froxlotl** (`mod/assets/froxlotl/`): model, rig and colour textures by **[TheIvoryDisaster](https://github.com/TheIvoryDisaster)**, made for PZA's mods
  (Meitou-Modding-Ecosystem/Husbandry PR #122); shipped here with their agreement. The gait and the bake are PZA's
  (`mod/tools/froxlotl/bake_froxlotl.py`, on PZA's Kenshi Motion Lab).
- **The birds' rig** (`mod/assets/birdrig/`): bird skeleton and animations by **[Mesh2Motion](https://mesh2motion.org)**
  ([Mesh2Motion/mesh2motion-app](https://github.com/Mesh2Motion/mesh2motion-app)), CC0 1.0; fitted and baked by PZA
  (`mod/tools/birds/bake_bird_rig.py`; `mod/tools/birds/meshopt_decode.py` reads its EXT_meshopt_compression files, written by PZA from
  the extension's public spec). The bird model itself is the game's, read from your install.
- **The Mire Stilt** (`mod/assets/stilt/`): models and textures by PZA (made with Meshy on PZA's own plan); skeleton and animations by
  **[Mesh2Motion](https://mesh2motion.org)** (CC0 1.0) with three neck bones, the wade and the hunt added by PZA
  (`mod/tools/stilt/stilt_rig.py`, `bake_stilt.py`).
- The frog's model and animations are the game's own Gorillo (`frog_gorilla.mesh` / `.skeleton`), read from the player's install at run
  time; no game file is copied or shipped.
- PZA's Meitou UI (same author): the string table (`PzaStrings`) is compiled in from source; its fuzz kit runs the tests.
- Russian, Simplified Chinese and Japanese texts are machine-drafted (by Claude, for PZA) and not yet checked by native speakers.
  Corrections are welcome and will be credited here.
