# PZA's Wildlife for Meitou: bakes Mesh2Motion's bird skeleton and clips (CC0 1.0, https://mesh2motion.org) into bone motion for the GAME's
# own bird (birdtype1.mesh). Author: PZA. Licence: GPLv3 or later.
#   python bake_bird_rig.py <mesh2motion-app static/assets/bird folder> <out dir> [--motionlab <PZAKenshiMotionLab folder>]
# Nothing of the game is read or written here: the fit uses only the game bird's published sizes (wingspan 52 units, wing roots about
# y 0.5, z -1; docs/game/birds.md and our own fold code). The mod weights the installed mesh to these bones when the game starts
# (BirdRig.cs): along each wing by span, along the body front to back.
# The fit: Mesh2Motion's bird scaled uniformly so its wingtips reach the game bird's (+-26 units), moved so its wing roots sit at the game
# bird's. A uniform scale keeps every bone rigid, so the motion is exactly Mesh2Motion's. Writes <out>/birdrig.pzab:
#   "PZABIRD1" + deflate( int bones; per bone: byte side (0 body, 1 left wing +x, 2 right wing -x), float key (|x| along a wing, z along
#   the body, at bind), float x, y, z (its bind position); int clips; per clip: byte name length, name, float seconds, int frames,
#   frames x bones x 12 floats (the 3x4 skin matrix: posed world x inverse bind world, rows of [R | t]) )
import argparse, os, struct, sys, tempfile, zlib
import numpy as np

WINGTIP, ROOT_Y, ROOT_Z = 26.0, 0.5, -1.0
CLIPS = [('glide', 8), ('flap', 12), ('land', 20), ('take-off', 16), ('hop', 10), ('idle', 10)]   # the eagle's order (EagleClip)
BODY = ['tail_tip', 'tail_3', 'tail_2', 'tail_1', 'hips', 'spine_1', 'spine_2', 'spine_3', 'head']
WING = ['wing_1_{}', 'wing_2_{}', 'wing_3_{}', 'wing_4_{}', 'wing_5_{}', 'wing_tip_{}']


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('bird'); ap.add_argument('out')
    ap.add_argument('--motionlab', default=os.path.join(os.path.dirname(__file__), '..', '..', '..', '..', 'PZAKenshiMotionLab'))
    a = ap.parse_args()
    sys.path.insert(0, os.path.abspath(a.motionlab))
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    from motionlab import gltf, bake
    import meshopt_decode
    tmp = tempfile.mkdtemp(prefix='pza_birdrig_')
    def plain(p):
        q = os.path.join(tmp, os.path.basename(p)); meshopt_decode.main(p, q); return q

    m = gltf.load_glb(plain(os.path.join(a.bird, 'model.glb')))
    sk = m.skeleton
    W0 = sk.world_rest()
    names = [n for n in BODY if n in sk.names] + [w.format('L') for w in WING] + [w.format('R') for w in WING]
    idx = [sk.index(n) for n in names]
    tip = W0[sk.index('wing_tip_L')][:3, 3]
    s = WINGTIP / float(tip[0])
    root = W0[sk.index('wing_1_L')][:3, 3]
    t = np.array([0.0, ROOT_Y - s * root[1], ROOT_Z - s * root[2]])

    def fit(W):
        F = W.copy()
        F[:, :3, 3] = W[:, :3, 3] * s + t
        return F
    B = fit(W0)
    Binv = np.array([np.linalg.inv(B[i]) for i in range(len(B))])

    body = bytearray(struct.pack('<i', len(names)))
    for n, i in zip(names, idx):
        side = 1 if n.endswith('_L') else 2 if n.endswith('_R') else 0
        p = B[i][:3, 3]
        key = abs(float(p[0])) if side else float(p[2])
        body += struct.pack('<Bffff', side, key, *[float(v) for v in p])
    body += struct.pack('<i', len(CLIPS))
    for name, frames in CLIPS:
        clips = gltf.read_clips(plain(os.path.join(a.bird, 'animations', name + '.glb')))
        _, nodes = next(iter(clips.items()))
        length = max(float(ch['times'][-1]) for ch in nodes.values() if 'times' in ch)
        nb = name.encode('ascii')
        body += struct.pack('<B', len(nb)) + nb + struct.pack('<fi', length, frames)
        for f in range(frames):
            P = fit(sk.world(bake.sample_read_clip(sk, nodes, length * f / frames)))
            for i in idx:
                M = P[i] @ Binv[i]
                body += np.asarray(M[:3, :4], '<f4').tobytes()
    co = zlib.compressobj(9, zlib.DEFLATED, -15)
    data = co.compress(bytes(body)) + co.flush()
    os.makedirs(a.out, exist_ok=True)
    with open(os.path.join(a.out, 'birdrig.pzab'), 'wb') as f:
        f.write(b'PZABIRD1'); f.write(data)
    print('birdrig.pzab: %d bones, %s, %.0f KB; scale %.2f, offset %s' % (len(names), ', '.join('%s %d' % c for c in CLIPS), (len(data) + 8) / 1024, s, t.round(2).tolist()))


if __name__ == '__main__':
    main()
