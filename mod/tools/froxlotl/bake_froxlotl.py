# PZA's Wildlife for Meitou: bakes the froxlotl's walk, run and rest into frames the mod draws. Author: PZA. Licence: GPLv3 or later.
#
# The froxlotl (model, rig and colour textures) is TheIvoryDisaster's, made for PZA's mods (Meitou-Modding-Ecosystem/Husbandry PR #122);
# the motion is PZA's: a salamander gait on that rig, written with PZA's Kenshi Motion Lab's model, skinning and quaternion code.
#   python bake_froxlotl.py <froxlotl-rigged.glb> <textures dir> <out dir> [--motionlab <PZAKenshiMotionLab folder>] [--gifs]
# Writes <out>/froxlotl.pzaf (the frames) and <out>/textures/<colour>.png (512 px). Plain python (numpy, Pillow), not -I.
#
# The gait. Mazal's rest pose lies curled; the tail's curl is undone first, then:
#   body  a travelling S-wave of bend about the world's up axis, head to tail, growing toward the tail; the root turns against the front
#         chain's bend so the head keeps its heading and the body wiggles behind it (a salamander's walk)
#   legs  diagonal pairs (front-left with hind-right) swing about the up axis at the hip, lifted in their swing half (eased in and out)
#   gills flutter at twice the beat
# Every bone only turns about the vertical: nothing leaves the ground. Frames are skinned on the CPU, turned so the head faces +z, centred,
# the lowest point of every frame at y = 0, scaled to LENGTH units long (1 unit = 10 cm).
import argparse, json, math, os, struct, sys, zlib
import numpy as np

LENGTH = 3.0                      # nose to tail tip at rest, in game units (30 cm)
CLIPS = {                          # name: (beats a second, frames, bend, wave number, stride, lift, tail gain)
    'rest': (0.45, 12, 0.035, 0.5, 0.0, 0.0, 1.6),
    'walk': (1.0, 20, 0.20, 0.65, 0.9, 0.6, 1.6),
    'run': (2.4, 16, 0.25, 0.72, 1.035, 0.72, 1.6),
    'swim': (1.3, 16, 0.30, 0.85, 0.0, 0.0, 2.0),      # legs tucked back along the body (TUCK), the tail drives (0.10.0, user: "swim away underwater")
}
TUCK = {'swim': 1.1}                                   # legs swept back (rad) and folded against the flanks
HEAD_HOLD, TRUNK = 0.85, 0.9
# the colours shipped: the wild one and the six tropical (the user left the pastels out, 2026-11-27); FroxPreset.Colours matches
COLOURS = ['original', 'parrot', 'sunset', 'lagoon', 'poison-dart', 'mango', 'hibiscus']
FRONT = ['Bone_006', 'Bone_005', 'Bone_004', 'Bone_015', 'Bone_014']                 # mid-body to the head
REAR = ['Bone_003', 'Bone_002', 'Bone_013', 'Bone_012', 'Bone_011', 'Bone_023', 'Bone_022', 'Bone_042', 'Bone_041']   # to the tail tip
TAIL_CURL = ('Bone_011', 'Bone_023', 'Bone_022', 'Bone_042', 'Bone_041')            # where the rest pose's curl is undone
LEGS = {'FL': ('Bone_029', 'Bone_028', 'L'), 'FR': ('Bone_027', 'Bone_026', 'R'), 'HL': ('Bone_010', 'Bone_009', 'L'), 'HR': ('Bone_008', 'Bone_007', 'R')}
GILLS = ['Bone_032', 'Bone_035', 'Bone_038']
UP, FWD = np.array([0.0, 1.0, 0.0]), np.array([0.0, 0.0, 1.0])


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('glb'); ap.add_argument('textures'); ap.add_argument('out')
    ap.add_argument('--motionlab', default=os.path.join(os.path.dirname(__file__), '..', '..', '..', '..', 'PZAKenshiMotionLab'))
    ap.add_argument('--gifs', action='store_true')
    a = ap.parse_args()
    sys.path.insert(0, os.path.abspath(a.motionlab))
    from motionlab import gltf, mathx as mx

    m = gltf.load_glb(a.glb)
    sk = m.skeleton; n = len(sk); names = sk.names
    W0 = sk.world_rest()
    idx = names.index
    pos = lambda b: W0[idx(b)][:3, 3]
    def rotm(i):
        R = W0[i][:3, :3]; return R / np.linalg.norm(R, axis=0)
    def local_axis(i, world_axis): return mx.norm(rotm(i).T @ world_axis)
    def turn_y(v1, v2):
        v1 = np.array([v1[0], 0, v1[2]]); v2 = np.array([v2[0], 0, v2[2]])
        return math.atan2(np.cross(v1, v2)[1], float(np.dot(v1, v2)))

    straight = {}
    ps = [pos('Bone_001')] + [pos(b) for b in REAR]
    for k in range(1, len(REAR)):
        if REAR[k] in TAIL_CURL:
            straight[REAR[k]] = -turn_y(ps[k] - ps[k - 1], ps[k + 1] - ps[k])
    chain = list(reversed(FRONT)) + ['Bone_001'] + REAR
    cp = np.array([pos(b) for b in chain])
    arc = np.r_[0, np.cumsum(np.linalg.norm(np.diff(cp, axis=0), axis=1))]; arc /= arc[-1]
    S = dict(zip(chain, arc))

    def locals_at(t, hz, bend, k_wave, stride, lift, tail_gain, tuck=0.0):
        ph = 2 * math.pi * hz * t
        q = [np.array(x, float) for x in sk.rest_q]
        wave = lambda s: bend * (TRUNK + tail_gain * s * s) * math.sin(ph - 2 * math.pi * k_wave * s)
        def turn(b, axis, ang):
            i = idx(b); q[i] = mx.q_mul(q[i], mx.q_axis_angle(local_axis(i, axis), ang))
        turn('Bone_000', UP, HEAD_HOLD * sum(wave(S[b]) for b in FRONT))
        for b in FRONT: turn(b, UP, -wave(S[b]))
        for b in REAR: turn(b, UP, straight.get(b, 0.0) + wave(S[b]))
        for key, (up_b, lo_b, side) in LEGS.items():
            sgn = -1.0 if side == 'L' else 1.0
            th = ph + (0.0 if key in ('FL', 'HR') else math.pi)
            sw = (0.5 + 0.5 * math.cos(th)) ** 2
            turn(up_b, UP, sgn * stride * math.sin(th))
            if tuck:    # swept back along the body and folded in
                back = tuck if key in ('HL', 'HR') else 0.8 * tuck
                turn(up_b, UP, -sgn * back)
                turn(lo_b, UP, -sgn * 0.6 * back)
            turn(up_b, FWD, -sgn * lift * sw)
            turn(lo_b, FWD, sgn * 0.6 * lift * sw)
        for g, b in enumerate(GILLS): turn(b, UP, 0.12 * math.sin(2 * ph + g))
        return [mx.trs(sk.rest_t[i], q[i], sk.rest_s[i]) for i in range(n)]

    # frames, skinned
    baked = {}
    for name, (hz, frames, bend, k, stride, lift, tg) in CLIPS.items():
        out = []
        for f in range(frames):
            W = sk.world(locals_at(f / (frames * hz), hz, bend, k, stride, lift, tg, TUCK.get(name, 0.0)))
            out.append(m.skin(W))
        baked[name] = (1.0 / hz, out)

    # the frame: head to +z (rest frame 0: mid-body to head), centred on x / z, scaled to LENGTH, each frame's floor at y = 0
    P0 = baked['rest'][1][0][0]
    fwd = pos('Bone_014') - pos('Bone_001'); yaw = math.atan2(fwd[0], fwd[2])
    c, s_ = math.cos(-yaw), math.sin(-yaw)
    R = np.array([[c, 0, s_], [0, 1, 0], [-s_, 0, c]])
    Q0 = P0 @ R.T
    scale = LENGTH / float(Q0[:, 2].max() - Q0[:, 2].min())
    centre = np.array([(Q0[:, 0].max() + Q0[:, 0].min()) / 2, 0, (Q0[:, 2].max() + Q0[:, 2].min()) / 2])
    def place(P, N):
        Q = (P @ R.T - centre) * scale
        Q[:, 1] -= Q[:, 1].min()
        return Q, N @ R.T

    # ground speeds: the planted feet slide back at the speed the body must move (vertices that follow the lower leg bones)
    J, Wt = m.joints, m.weights
    feet = np.zeros(len(m.verts), bool)
    for _, lo_b, _ in LEGS.values():
        feet |= ((J == idx(lo_b)) * Wt).sum(1) > 0.6
    speeds = {}
    for name, (length, frames) in baked.items():
        Z = np.array([place(P, N)[0][feet] for P, N in frames])
        dt = length / len(frames)
        v = -(np.roll(Z[:, :, 2], -1, axis=0) - Z[:, :, 2]) / dt      # backward slide, per foot vertex
        low = np.roll(Z[:, :, 1], -1, axis=0) < 0.05 * LENGTH          # planted: near the ground
        speeds[name] = float(np.median(v[low & (v > 0)])) if (low & (v > 0)).any() else 0.0
    print('ground speeds (units/s):', {k: round(v, 2) for k, v in speeds.items()}, 'scale', round(scale, 4))

    os.makedirs(os.path.join(a.out, 'textures'), exist_ok=True)
    body = bytearray()
    V, T, I = len(m.verts), m.uvs, m.tris.reshape(-1).astype(np.uint32)
    body += struct.pack('<iif', V, len(I), LENGTH)
    body += struct.pack('<fff', speeds.get('rest', 0.0), speeds.get('walk', 0.0), speeds.get('run', 0.0))
    body += np.asarray(T, '<f4').tobytes() + I.astype('<u4').tobytes()
    body += struct.pack('<i', len(baked))
    for name, (length, frames) in baked.items():
        nb = name.encode('ascii')
        body += struct.pack('<B', len(nb)) + nb + struct.pack('<fi', length, len(frames))
        for P, N in frames:
            Q, M = place(P, N)
            rec = np.zeros(V, dtype=[('p', '<f2', 3), ('n', 'i1', 3)])
            rec['p'] = Q.astype(np.float16)
            rec['n'] = np.clip(np.round(M * 127), -127, 127).astype(np.int8)
            body += rec.tobytes()
    co = zlib.compressobj(9, zlib.DEFLATED, -15)
    data = co.compress(bytes(body)) + co.flush()
    with open(os.path.join(a.out, 'froxlotl.pzaf'), 'wb') as f:
        f.write(b'PZAFROX1'); f.write(data)
    print('froxlotl.pzaf: %d vertices, %d triangles, %s, %.0f KB' % (V, len(I) // 3, ', '.join('%s %d' % (k, len(v[1])) for k, v in baked.items()), (len(data) + 8) / 1024))

    from PIL import Image
    for f in sorted(os.listdir(a.textures)):
        if not f.endswith('.png') or f[:-4] not in COLOURS: continue
        Image.open(os.path.join(a.textures, f)).convert('RGBA').resize((512, 512), Image.LANCZOS).save(os.path.join(a.out, 'textures', f), optimize=True)
    print('textures:', len([f for f in os.listdir(os.path.join(a.out, 'textures'))]))

    if a.gifs:      # a quick look: every frame from above and beside, through the Motion Lab's renderer
        from motionlab import render
        from PIL import Image as Im
        for name, (length, frames) in baked.items():
            for view, (yw, pt) in {'top': (10.0, 70.0), 'side': (38.0, 14.0)}.items():
                cam = render.Camera(np.array([0, 0.1 * LENGTH, 0]), yw, pt, 1.6 * LENGTH)
                imgs = []
                mm = m
                for rep in range(3):
                    for P, N in frames:
                        Q, M = place(P, N)
                        mm.verts, mm.normals = Q, M
                        imgs.append(render.Renderer(mm, cam).frame(Q, M))
                render.save_gif(imgs, os.path.join(a.out, '..', '%s_%s.gif' % (name, view)), fps=round(len(frames) / length))


if __name__ == '__main__':
    main()
