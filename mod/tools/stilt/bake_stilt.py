# PZA's Wildlife for Meitou: bakes the Mire Stilt's clips into frames the mod draws. Author: PZA. Licence: GPLv3 or later.
#
# The two meshes are the user's own Meshy models (Meshy Pro): the flight model (mire_stilt_v1) and the standing one made from its look
# with the membrane wings folded (mire_stilt_standing_v2). The rig and clips are Mesh2Motion's CC0 bird plus PZA's neck bones, wade and
# hunt (stilt_rig.py). Two bakes, one per mesh, in the froxlotl's format (FroxAsset, "PZAFROX1"):
#   stilt_ground.pzaf  idle, wade, hunt, takeoff_head (the take-off's first 12%), land_tail (the landing's last 10%)
#   stilt_air.pzaf     takeoff_tail, flap, glide, land_head
# Both in game units (1 unit = 10 cm), +z forward, the origin on the ground under the standing bird; the take-off and landing carry the
# bird's own rise and fall, so the two meshes meet at the swap.
#   python bake_stilt.py <meshy mire_stilt folder> <m2m bird plain folder> <out dir> [--gifs]
# Writes <out>/stilt_ground.pzaf, <out>/stilt_air.pzaf, <out>/textures/ground.png and air.png (1024 px). Plain python (numpy, Pillow).
import os, sys, struct, zlib, math
import numpy as np

SRC, BIRD, OUT = sys.argv[1], sys.argv[2], sys.argv[3]
GIFS = '--gifs' in sys.argv
sys.argv = [sys.argv[0], SRC, BIRD, OUT, 'none']       # stilt_rig builds the rig at import; 'none' renders nothing
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import stilt_rig as R
from PIL import Image

UNIT = 10.0                                          # game units per metre


def frames_of(mesh, n, pose_at, floor=False):
    """n frames, skinned, in game units. floor: never below the ground (the take-off's crouch lowers the hips, which lift() reads as
    the whole bird sinking: the feet stay on the ground instead)."""
    out = []
    for k in range(n):
        posed = pose_at(k)
        P, N = mesh.skin(posed)
        P = P * UNIT
        if floor and P[:, 1].min() < 0: P[:, 1] -= P[:, 1].min()
        out.append((P, N))
    return out


def ground_clips():
    st, IW = R.stand, R.IW
    base = R.ground_base(st, IW)
    clips = {}
    n = int(R.idle_len * 15)
    clips['idle'] = (R.idle_len, frames_of(st, n, lambda k: st.pose(W := R.bird_world(R.idle, R.idle_len * k / n), root_shift=R.lift(st, W, base))))
    nd, ln = R.clip('walk'); wl = ln / 0.6; n = int(wl * 20)
    def wade(k):
        W = R.highstep(nd, ln, wl * k / n * 1.0)
        return st.pose(W, root_shift=R.feet_on_ground(st, st.pose(W)))
    clips['wade'] = (wl, frames_of(st, n, wade))
    n = int(R.HUNT_LEN * 15)
    def hunt(k):
        W, e = R.hunt(R.HUNT_LEN * k / n)
        return st.pose(W, e, root_shift=R.feet_on_ground(st, st.pose(W, e)))
    clips['hunt'] = (R.HUNT_LEN, frames_of(st, n, hunt))
    to, tl = R.clip('take-off'); head = 0.12 * tl; n = max(2, int(head * 20))
    clips['takeoff_head'] = (head, frames_of(st, n, lambda k: st.pose(W := R.bird_world(to, head * k / n), root_shift=R.lift(st, W, base)), floor=True))
    la, ll = R.clip('land'); t0 = 0.9 * ll; tail = ll - t0; n = max(2, int(tail * 20))
    clips['land_tail'] = (tail, frames_of(st, n, lambda k: st.pose(W := R.bird_world(la, t0 + tail * k / n), root_shift=R.lift(st, W, base)), floor=True))
    return clips


def air_clips():
    fl = R.fly
    la, ll = R.clip('land'); LE = R.bird_world(la, ll * 0.999)
    base = R.ground_base(fl, LE)
    clips = {}
    to, tl = R.clip('take-off'); t0 = 0.12 * tl; part = tl - t0; n = int(part * 30)      # 30 fps: the clip's own rise and fall are fast
    clips['takeoff_tail'] = (part, frames_of(fl, n, lambda k: fl.pose(W := R.bird_world(to, t0 + part * k / n), root_shift=R.lift(fl, W, base)), floor=True))
    for name in ('flap', 'glide'):
        nd, ln = R.clip(name); n = int(ln * 20)
        clips[name] = (ln, frames_of(fl, n, lambda k, nd=nd, ln=ln: fl.pose(W := R.bird_world(nd, ln * k / n), root_shift=R.lift(fl, W, base))))
    head = 0.9 * ll; n = int(head * 30)
    clips['land_head'] = (head, frames_of(fl, n, lambda k: fl.pose(W := R.bird_world(la, head * k / n), root_shift=R.lift(fl, W, base)), floor=True))
    return clips


def wade_speed(clips, mesh):
    """The wade's ground speed (units/s): each foot's toe tip (its lowest-reaching vertex) slides back while it is planted (within
    0.3 units of the ground): that slide's distance over its time, both feet averaged."""
    length, frames = clips['wade']
    Z = np.array([P for P, _ in frames]); dt = length / len(frames)
    speeds = []
    for sgn in (1, -1):
        side = np.where(sgn * mesh.V[:, 0] > 0.02)[0]
        toe = side[np.argmin(Z[:, side, 1].max(0))]          # the vertex that stays lowest over the cycle on this side
        y, z = Z[:, toe, 1], Z[:, toe, 2]
        down = y < y.min() + 0.3
        if down.sum() >= 2:
            zs = z[down]; speeds.append(float(zs.max() - zs.min()) / (down.sum() * dt))
    return float(np.mean(speeds)) if speeds else 0.0


def write(path, mesh, clips, walk_speed):
    V = len(mesh.V); I = mesh.m.tris.reshape(-1).astype(np.uint32); T = mesh.m.uvs
    height = 2.1 * UNIT
    body = bytearray(struct.pack('<iif', V, len(I), height) + struct.pack('<fff', 0.0, walk_speed, 0.0))
    body += np.asarray(T, '<f4').tobytes() + I.astype('<u4').tobytes()
    body += struct.pack('<i', len(clips))
    for name, (length, frames) in clips.items():
        nb = name.encode('ascii')
        body += struct.pack('<B', len(nb)) + nb + struct.pack('<fi', length, len(frames))
        for P, N in frames:
            rec = np.zeros(V, dtype=[('p', '<f2', 3), ('n', 'i1', 3)])
            rec['p'] = P.astype(np.float16)
            rec['n'] = np.clip(np.round(N * 127), -127, 127).astype(np.int8)
            body += rec.tobytes()
    co = zlib.compressobj(9, zlib.DEFLATED, -15)
    data = co.compress(bytes(body)) + co.flush()
    with open(path, 'wb') as f:
        f.write(b'PZAFROX1'); f.write(data)
    print('%s: %d vertices, %d triangles, %s, %.0f KB' % (os.path.basename(path), V, len(I) // 3,
          ', '.join('%s %d (%.2f s)' % (k, len(v[1]), v[0]) for k, v in clips.items()), (len(data) + 8) / 1024))


os.makedirs(os.path.join(OUT, 'textures'), exist_ok=True)
g, a = ground_clips(), air_clips()
speed = wade_speed(g, R.stand)
print('wade ground speed %.2f units/s; flight mesh %.2f m/unit' % (speed, R.fly.mpu))
# the swap must meet: the standing mesh's last take-off-head frame and the flight mesh's first take-off-tail frame, lowest points
for (gn, gi), (an, ai) in ((('takeoff_head', -1), ('takeoff_tail', 0)), (('land_tail', 0), ('land_head', -1))):
    print('swap %s/%s: lowest point %.2f vs %.2f units' % (gn, an, g[gn][1][gi][0][:, 1].min(), a[an][1][ai][0][:, 1].min()))
write(os.path.join(OUT, 'stilt_ground.pzaf'), R.stand, g, speed)
write(os.path.join(OUT, 'stilt_air.pzaf'), R.fly, a, 0.0)
for src, dst in (('mire_stilt_standing_v2_base_color.png', 'ground.png'), ('mire_stilt_v1_base_color.png', 'air.png')):
    Image.open(os.path.join(SRC, src)).convert('RGBA').resize((1024, 1024), Image.LANCZOS).save(os.path.join(OUT, 'textures', dst), optimize=True)
print('textures: ground.png, air.png')
