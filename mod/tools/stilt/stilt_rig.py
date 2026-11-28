# PZA's Wildlife for Meitou: the Mire Stilt's rig. Author: PZA. Licence: GPLv3 or later.
# Mire Stilt: two Meshy meshes (the user's own, Meshy Pro) on Mesh2Motion's CC0 bird rig plus three neck bones (PZA).
#   flight mesh (mire_stilt_v1.glb): bound to the bird's glide pose, plays take-off / flap / glide / land
#   standing mesh (mire_stilt_standing_v1.glb): bound to the bird's idle pose, plays idle, a slowed high-step wade, the hunting stance,
#   the stab and the swallow; the two swap inside take-off and landing. Both scaled to 2.1 m standing (the user's pick).
#   python stilt_rig.py <meshy mire_stilt folder> <m2m bird plain folder> <out dir> [flight,landing,wade,fishing]   previews (GIFs)
#   bake_stilt.py imports it (want = none) for the mod's frames. The game meshes (6k faces, decimate_glb.py) carry the weights; the
#   landmarks come from the finer meshes.
import sys, os, math, numpy as np
sys.path.insert(0, os.environ.get('PZA_MOTIONLAB', os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', '..', '..', 'PZAKenshiMotionLab')))
from motionlab import gltf, bake, render, mathx as mx
from PIL import ImageDraw

SRC, BIRD, OUT = sys.argv[1], sys.argv[2], sys.argv[3]
want = sys.argv[4].split(',') if len(sys.argv) > 4 else ['flight', 'landing', 'wade', 'fishing']
STAND_M = 2.1
bird = gltf.load_glb(os.path.join(BIRD, 'model.glb')); bk = bird.skeleton; bnames = bk.names
def rot(M):
    R = M[:3, :3]; return R / np.linalg.norm(R, axis=0)
def clip(name):
    c = gltf.read_clips(os.path.join(BIRD, 'animations', name + '.glb')); nd = next(iter(c.values()))
    return nd, max(float(ch['times'][-1]) for ch in nd.values() if 'times' in ch)
def bird_world(nd, t, extra=None):
    loc = bake.sample_read_clip(bk, nd, t)
    return bk.world(loc)
NECK = ['neck_1', 'neck_2', 'neck_3']
# the skeleton with the neck: parent of each bone by name (head re-parented under neck_3)
PARENT = {n: (bnames[p] if p >= 0 else None) for n, p in zip(bnames, bk.parents)}
PARENT.update({'neck_1': 'spine_3', 'neck_2': 'neck_1', 'neck_3': 'neck_2', 'head': 'neck_3'})
NAMES = bnames + NECK
def order():
    out, seen = [], set()
    def visit(n):
        if n in seen: return
        if PARENT[n]: visit(PARENT[n])
        seen.add(n); out.append(n)
    for n in NAMES: visit(n)
    return out
ORDER = order()

def world_rots(W):
    """Bird world rotations by name, the neck bones eased between spine_3 and head."""
    R = {n: rot(W[bk.index(n)]) for n in bnames}
    qa, qb = mx.m3_to_q(R['spine_3']), mx.m3_to_q(R['head'])
    for k, n in enumerate(NECK):
        R[n] = mx.q_to_m3(mx.q_slerp(qa, qb, (k + 1) / 4))
    return R

class Rigged:
    def __init__(self, path, landmarks, bind_W, regions, metres_per_unit, xform=None):
        self.m = gltf.load_glb(path)
        if xform is not None: self.m.verts = xform(self.m.verts)
        self.V = self.m.verts.astype(float); self.L = landmarks
        self.mpu = metres_per_unit
        self.Rb = world_rots(bind_W)
        # weights: nearest bone segments of the vertex's region
        kids = {}
        for n in NAMES:
            if PARENT[n]: kids.setdefault(PARENT[n], []).append(n)
        J = np.zeros((len(self.V), 3), int); Wt = np.zeros((len(self.V), 3))
        idx = {n: i for i, n in enumerate(NAMES)}
        for vi, v in enumerate(self.V):
            cands = []
            for n in regions(v):
                c = [k for k in kids.get(n, []) if 'feather' not in k]
                b = landmarks[c[0]] if c else landmarks[n]
                a = landmarks[n]; ab = b - a
                t = np.clip(np.dot(v - a, ab) / max(np.dot(ab, ab), 1e-12), 0, 1)
                cands.append((np.linalg.norm(v - (a + t * ab)), idx[n]))
            cands.sort(); d = np.array([c[0] for c in cands[:3]]); w = 1 / (d * d + 1e-7); w /= w.sum()
            for k in range(len(d)): J[vi, k] = cands[k][1]; Wt[vi, k] = w[k]
        self.J, self.Wt = J, Wt
        self.bind_inv = []
        for n in NAMES:
            M = np.eye(4); M[:3, :3] = self.Rb[n]; M[:3, 3] = landmarks[n]; self.bind_inv.append(np.linalg.inv(M))

    def pose(self, W, extra=None, root_shift=None):
        """Bone world matrices: the clip's world rotations (and authored extra world rotations, cumulative down the chain), the offsets
        from this mesh's own landmarks."""
        R = world_rots(W); extra = extra or {}
        acc, out = {}, {}
        for n in ORDER:
            p = PARENT[n]
            E = (acc[p] if p else np.eye(3)) @ extra.get(n, np.eye(3))
            acc[n] = E
            Rn = E @ R[n]
            if p is None:
                pos = self.L[n].copy()
            else:
                pos = out[p][:3, 3] + out[p][:3, :3] @ self.Rb[p].T @ (self.L[n] - self.L[p])
            M = np.eye(4); M[:3, :3] = Rn; M[:3, 3] = pos; out[n] = M
        if root_shift is not None:
            for n in out: out[n][:3, 3] += root_shift
        return out

    def skin(self, posed):
        S = np.array([posed[n] @ self.bind_inv[i] for i, n in enumerate(NAMES)])
        hp = np.c_[self.V, np.ones(len(self.V))]
        P = np.zeros_like(self.V); N = np.zeros_like(self.V)
        for k in range(3):
            M = S[self.J[:, k]]
            P += self.Wt[:, k:k + 1] * np.einsum('nij,nj->ni', M[:, :3, :], hp)
            N += self.Wt[:, k:k + 1] * np.einsum('nij,nj->ni', M[:, :3, :3], self.m.normals)
        return P * self.mpu, mx.norm(N)

# ---------------- landmarks ----------------
def flight_landmarks(V):
    lo, hi = V.min(0), V.max(0); span = hi[0]
    narrow = np.abs(V[:, 0]) < 0.03
    body = (np.abs(V[:, 0]) < 0.09) & (V[:, 2] > -0.15) & (V[:, 2] < 0.06) & (V[:, 1] > -0.07)
    bc = V[body].mean(0)
    def along(z):
        s = V[narrow & (np.abs(V[:, 2] - z) < 0.015)]; return np.array([0.0, s[:, 1].mean() if len(s) else bc[1], z])
    L = {'root': np.array([0, lo[1], bc[2]]), 'hips': bc + [0, 0, -0.05], 'spine_0': bc + [0, 0, -0.01], 'spine_1': bc + [0, 0, 0.03],
         'spine_2': along(0.06), 'spine_3': along(0.09), 'neck_1': along(0.115), 'neck_2': along(0.14), 'neck_3': along(0.16),
         'head': along(0.185), 'mouth_upper': along(0.21), 'mouth_lower': along(0.215) + [0, -0.008, 0]}
    L['mouth_upper_tip'] = along(hi[2] - 0.005); L['mouth_lower_tip'] = L['mouth_upper_tip'] + [0, -0.006, -0.01]
    tail = narrow & (V[:, 2] < -0.12) & (V[:, 1] > -0.03)
    tc = V[tail].mean(0) if tail.any() else bc + [0, 0, -0.12]
    for k, n in enumerate(['tail_1', 'tail_2', 'tail_3', 'tail_tip']): L[n] = L['hips'] + (tc + [0, 0, -0.03] - L['hips']) * (k + 1) / 4
    P0 = {n: bk.world_rest()[bk.index(n)][:3, 3] for n in bnames}
    for side, sgn in (('L', 1), ('R', -1)):
        for n in ['wing_1', 'wing_2', 'wing_3', 'wing_4', 'wing_5', 'wing_tip']:
            frac = abs(P0[f'{n}_{side}'][0]) / abs(P0[f'wing_tip_{side}'][0]); x = max(0.03, frac * span * 0.97)
            s = V[(np.abs(V[:, 0] - sgn * x) < 0.03) & (sgn * V[:, 0] > 0)]
            L[f'{n}_{side}'] = np.array([sgn * x, s[:, 1].mean(), s[:, 2].max() - 0.01])
        leg = V[(sgn * V[:, 0] > 0) & (np.abs(V[:, 0]) < 0.09) & (V[:, 2] < -0.12) & (V[:, 1] < -0.03)]
        hip = np.array([sgn * 0.03, bc[1] - 0.03, -0.11]); far = leg[np.argmax(np.linalg.norm(leg - hip, axis=1))]
        for n, f in (('UpperLeg', 0.0), ('LowerLeg', 0.3), ('AnkleLeg', 0.62), ('Foot', 0.86), ('Toes', 0.94), ('Toes_tip', 1.0)):
            L[f'{n}_{side}'] = hip + (far - hip) * f
    fill(L, span / abs(P0['wing_tip_L'][0]))
    def regions(v):
        if abs(v[0]) > 0.075 and v[1] > -0.06: return [n for n in bnames if n.startswith('wing_') and n.endswith('_L' if v[0] > 0 else '_R') and 'feather' not in n]
        if v[2] < -0.12 and v[1] < -0.03 and abs(v[0]) < 0.09:
            s = 'L' if v[0] >= 0 else 'R'; return [f'UpperLeg_{s}', f'LowerLeg_{s}', f'AnkleLeg_{s}', f'Foot_{s}', f'Toes_{s}']
        return ['hips', 'spine_0', 'spine_1', 'spine_2', 'spine_3'] + NECK + ['head', 'mouth_upper', 'mouth_lower', 'tail_1', 'tail_2', 'tail_3']
    return L, regions

def standing_landmarks(V):
    lo, hi = V.min(0), V.max(0)
    torso = (V[:, 1] > -0.16) & (V[:, 1] < 0.13) & (np.abs(V[:, 0]) < 0.07)
    tcz = float(V[torso][:, 2].mean())
    def neck_at(y):
        s = V[(np.abs(V[:, 1] - y) < 0.02) & (np.abs(V[:, 0]) < 0.04) & (V[:, 2] < 0.1)]
        return np.array([0.0, y, float(s[:, 2].mean()) if len(s) else tcz])
    L = {'root': np.array([0, lo[1], tcz]), 'hips': np.array([0, -0.10, tcz - 0.03]), 'spine_0': np.array([0, -0.04, tcz]),
         'spine_1': np.array([0, 0.02, tcz + 0.01]), 'spine_2': neck_at(0.10), 'spine_3': neck_at(0.16), 'neck_1': neck_at(0.21),
         'neck_2': neck_at(0.26), 'neck_3': neck_at(0.31), 'head': neck_at(0.35)}
    beak = V[(V[:, 1] > 0.25) & (V[:, 2] > 0.12)]
    tip = beak[np.argmax(beak[:, 2])]
    L['mouth_upper'] = np.array([0, tip[1] + 0.02, 0.12]); L['mouth_lower'] = L['mouth_upper'] + [0, -0.012, 0.005]
    L['mouth_upper_tip'] = tip.copy(); L['mouth_lower_tip'] = tip + [0, -0.01, -0.01]
    tailv = V[(V[:, 2] < -0.25)]
    tc = tailv.mean(0) if len(tailv) else np.array([0, -0.15, -0.33])
    for k, n in enumerate(['tail_1', 'tail_2', 'tail_3', 'tail_tip']): L[n] = L['hips'] + (tc - L['hips']) * (k + 1) / 4
    for side, sgn in (('L', 1), ('R', -1)):
        legv = V[(sgn * V[:, 0] > 0.005) & (V[:, 1] < -0.17)]
        def at(y):
            s = legv[np.abs(legv[:, 1] - y) < 0.015]; return np.array([s[:, 0].mean(), y, s[:, 2].mean()]) if len(s) else np.array([sgn * 0.04, y, tcz])
        L[f'UpperLeg_{side}'] = np.array([sgn * 0.045, -0.15, tcz - 0.02]); L[f'LowerLeg_{side}'] = at(-0.24)
        L[f'AnkleLeg_{side}'] = at(-0.33); L[f'Foot_{side}'] = at(-0.455)
        toe = legv[legv[:, 1] < -0.44]; tz = toe[np.argmax(toe[:, 2])] if len(toe) else L[f'Foot_{side}'] + [0, 0, 0.05]
        L[f'Toes_{side}'] = L[f'Foot_{side}'] + (tz - L[f'Foot_{side}']) * 0.5; L[f'Toes_tip_{side}'] = tz
        # folded wings ride the body: their bones sit along the flank (only the drying pose would open them)
        for k, n in enumerate(['wing_1', 'wing_2', 'wing_3', 'wing_4', 'wing_5', 'wing_tip']):
            L[f'{n}_{side}'] = np.array([sgn * 0.11, 0.08 - 0.06 * k, tcz - 0.05 * k])
    fill(L, 0.45)
    def regions(v):
        if v[1] < -0.165 and abs(v[0]) < 0.12:
            s = 'L' if v[0] >= 0 else 'R'; return [f'UpperLeg_{s}', f'LowerLeg_{s}', f'AnkleLeg_{s}', f'Foot_{s}', f'Toes_{s}']
        return ['hips', 'spine_0', 'spine_1', 'spine_2', 'spine_3'] + NECK + ['head', 'mouth_upper', 'mouth_lower', 'tail_1', 'tail_2', 'tail_3']
    return L, regions

_NORM = {}
def norm_stand(V):
    """A standing mesh in the first standing mesh's frame: height 0.951, y centred (the v2 model comes at twice the size). The frame is
    fixed by the finer mesh (the first call), so the game mesh lands exactly on its landmarks."""
    V = np.asarray(V, float).copy()
    if not _NORM: _NORM['lo'], _NORM['hi'] = float(V[:, 1].min()), float(V[:, 1].max())
    lo, hi = _NORM['lo'], _NORM['hi']
    return (V - [0, (hi + lo) / 2, 0]) * 0.951 / (hi - lo)

def standing_landmarks_v2(V):
    """The v2 standing model (the flight model's look, membrane wings folded flat; user 2026-11-27): a near-upright neck at
    z ~0.16, the head turned a little, folded wing tips trailing to the heels (they ride the tail bones)."""
    tip = V[(V[:, 1] > 0.22) & (V[:, 2] > 0.22)]; tip = tip[np.argmax(tip[:, 2])]
    def neck_at(y, z0):
        s = V[(np.abs(V[:, 1] - y) < 0.012) & (np.abs(V[:, 0]) < 0.07) & (V[:, 2] > 0.1) & (V[:, 2] < 0.24)]
        return np.array([float(s[:, 0].mean()) if len(s) else 0.0, y, float(s[:, 2].mean()) if len(s) else z0])
    L = {'root': np.array([0, V[:, 1].min(), 0.02]), 'hips': np.array([0, -0.08, 0.0]), 'spine_0': np.array([0, -0.02, 0.04]),
         'spine_1': np.array([0, 0.05, 0.08]), 'spine_2': np.array([0, 0.13, 0.13]), 'spine_3': np.array([0, 0.20, 0.16]),
         'neck_1': neck_at(0.26, 0.17), 'neck_2': neck_at(0.31, 0.155), 'neck_3': neck_at(0.36, 0.155), 'head': neck_at(0.405, 0.155)}
    L['mouth_upper'] = np.array([L['head'][0], 0.426, 0.19]); L['mouth_lower'] = L['mouth_upper'] + [0, -0.015, 0.005]
    L['mouth_upper_tip'] = tip.copy(); L['mouth_lower_tip'] = tip + [0, -0.01, -0.01]
    tc = np.array([0.0, -0.18, -0.32])
    for k, n in enumerate(['tail_1', 'tail_2', 'tail_3', 'tail_tip']): L[n] = L['hips'] + (tc - L['hips']) * (k + 1) / 4
    for side, sgn in (('L', 1), ('R', -1)):
        legv = V[(sgn * V[:, 0] > 0.01) & (np.abs(V[:, 0]) < 0.085) & (V[:, 2] > -0.06) & (V[:, 1] < -0.12)]
        def at(y):
            s = legv[np.abs(legv[:, 1] - y) < 0.015]; return np.array([s[:, 0].mean(), y, s[:, 2].mean()]) if len(s) else np.array([sgn * 0.05, y, 0.0])
        L[f'UpperLeg_{side}'] = at(-0.13); L[f'LowerLeg_{side}'] = at(-0.22)
        L[f'AnkleLeg_{side}'] = at(-0.32); L[f'Foot_{side}'] = at(-0.445)
        toe = legv[legv[:, 1] < -0.44]; tz = toe[np.argmax(toe[:, 2])] if len(toe) else L[f'Foot_{side}'] + [0, 0, 0.08]
        L[f'Toes_{side}'] = L[f'Foot_{side}'] + (tz - L[f'Foot_{side}']) * 0.5; L[f'Toes_tip_{side}'] = tz
        for k, n in enumerate(['wing_1', 'wing_2', 'wing_3', 'wing_4', 'wing_5', 'wing_tip']):
            L[f'{n}_{side}'] = np.array([sgn * 0.12, 0.12 - 0.07 * k, 0.05 - 0.07 * k])
    fill(L, 0.45)
    # the legs are thin: a vertex is leg only within 0.03 (horizontally) of its leg's centre line at its own height, or in the feet;
    # the folded wing tips hanging beside the shins stay on the body (the wade dragged them with the legs)
    line = {}
    for side, sgn in (('L', 1), ('R', -1)):
        legv = V[(sgn * V[:, 0] > 0.01) & (np.abs(V[:, 0]) < 0.085) & (V[:, 2] > -0.06) & (V[:, 1] < -0.12)]
        pts = []
        for y in np.arange(-0.44, -0.11, 0.01):
            sl = legv[np.abs(legv[:, 1] - y) < 0.008]
            if len(sl): pts.append((y, float(np.median(sl[:, 0])), float(np.median(sl[:, 2]))))
        line[side] = np.array(pts)
    def regions(v):
        s = 'L' if v[0] >= 0 else 'R'
        legs = [f'UpperLeg_{s}', f'LowerLeg_{s}', f'AnkleLeg_{s}', f'Foot_{s}', f'Toes_{s}']
        if v[1] < -0.415 and abs(v[0]) < 0.14 and v[2] > -0.1: return legs
        if v[1] < -0.13:
            ln = line[s]; k = int(np.argmin(np.abs(ln[:, 0] - v[1])))
            if abs(ln[k, 0] - v[1]) < 0.012 and math.hypot(v[0] - ln[k, 1], v[2] - ln[k, 2]) < 0.03: return legs
        return ['hips', 'spine_0', 'spine_1', 'spine_2', 'spine_3'] + NECK + ['head', 'mouth_upper', 'mouth_lower', 'tail_1', 'tail_2', 'tail_3']
    return L, regions

def fill(L, scale):
    P0 = {n: bk.world_rest()[bk.index(n)][:3, 3] for n in bnames}
    for n in bnames:
        if n not in L:
            par = PARENT[n]; L[n] = L.get(par, np.zeros(3)) + (P0[n] - P0[par]) * scale

# ---------------- the two meshes, scaled to 2.1 m standing ----------------
glide, _ = clip('glide'); idle, idle_len = clip('idle')
GW = bird_world(glide, 0.0); IW = bird_world(idle, 0.0)
STAND_FINE = 'mire_stilt_standing_v2_lod.glb'    # v2: the flight model's look, wings folded (Meshy, decimated to 20k faces): landmarks
STAND_GLB = 'mire_stilt_standing_v2_game.glb'    # the same at 6k faces: drawn in game
FLY_GLB = 'mire_stilt_v1_game.glb'
SV = norm_stand(gltf.load_glb(os.path.join(SRC, STAND_FINE)).verts)
stand_mpu = STAND_M / float(SV[:, 1].max() - SV[:, 1].min())
FV = gltf.load_glb(os.path.join(SRC, 'mire_stilt_v1.glb')).verts
fl_mpu = 3.2 / float(FV[:, 0].max() - FV[:, 0].min()) * 1.27      # pass 1's 1.66 m fit x1.27 = 2.1 m standing
Ls, rs = standing_landmarks_v2(SV); stand = Rigged(os.path.join(SRC, STAND_GLB), Ls, IW, rs, stand_mpu, xform=norm_stand)
Lf, rf = flight_landmarks(FV); fly = Rigged(os.path.join(SRC, FLY_GLB), Lf, GW, rf, fl_mpu)
# the two Meshy models differ in proportion: size the flight mesh by its BODY (wings left out), both posed in the idle, so the bird
# keeps its size across the swap (the user saw the wingspan fit leave the flight body ~23% small)
def head_height(m):
    """The head bone's height over the feet, both meshes posed in the bird's idle: wings (spread or folded) can't skew it."""
    posed = m.pose(IW); P, _ = m.skin(posed); return float(posed['head'][1, 3] * m.mpu - P[:, 1].min())
_k = head_height(stand) / head_height(fly)
fly.mpu *= _k; fl_mpu = fly.mpu
print('scale: standing %.2f m/unit, flight %.2f m/unit (head-height fit x%.3f, wingspan %.2f m)' % (stand_mpu, fl_mpu, _k, (FV[:, 0].max() - FV[:, 0].min()) * fl_mpu))

def xrot(a): return mx.q_to_m3(mx.q_axis_angle(np.array([1.0, 0, 0]), a))

def highstep(nd, length, t, slow=0.6, lift=1.45):
    """The bird's walk, slowed, its leg swings amplified (high wading steps)."""
    loc = bake.sample_read_clip(bk, nd, (t * slow) % length)
    rest = bake.sample_read_clip(bk, idle, 0.0)
    for side in 'LR':
        for n in (f'UpperLeg_{side}', f'LowerLeg_{side}', f'AnkleLeg_{side}'):
            i = bk.index(n)
            qr = mx.m3_to_q(rest[i][:3, :3]); qa = mx.m3_to_q(loc[i][:3, :3])
            d = mx.q_mul(qa, mx.q_conj(qr)); w = max(-1.0, min(1.0, d[3])); ang = 2 * math.acos(w)
            if ang > 1e-5:
                ax = np.array(d[:3]) / math.sin(ang / 2)
                loc[i][:3, :3] = mx.q_to_m3(mx.q_mul(mx.q_axis_angle(ax, ang * lift), qr))
    return bk.world(loc)

def yrot(a): return mx.q_to_m3(mx.q_axis_angle(np.array([0, 1.0, 0]), a))
def zrot(a): return mx.q_to_m3(mx.q_axis_angle(np.array([0, 0, 1.0]), a))
def sm(x): x = min(1.0, max(0.0, x)); return x * x * (3 - 2 * x)
def window(t, a, b, c, d):
    """0 before a, eased up to 1 by b, held, eased down to 0 from c to d."""
    return sm((t - a) / (b - a)) * (1 - sm((t - c) / (d - c)))

HUNT_LEN = 5.4
def strike_curve(t):
    """The stab: a held coil, then a 0.15 s ACCELERATING throw (ease-in, a heron's strike is fastest at the end) with an 8% overshoot,
    held under the water while the head shakes, then an ease-out lift."""
    if t < 2.75: return 0.0
    if t < 2.9: x = (t - 2.75) / 0.15; return x * x
    if t < 3.0: return 1.0 + 0.08 * math.sin(math.pi * (t - 2.9) / 0.1)
    if t < 3.35: return 1.0
    if t < 3.8: x = (t - 3.35) / 0.45; return 1 - (1 - (1 - x) ** 3)
    return 0.0

def hunt(t):
    """PZA's fishing set on the idle (seconds from 0), heron-style: lean in, stalk (the head steady, a slow creep forward, a cocked
    head, small scans), coil back, an accelerating strike with overshoot, a shake under the water, lift, beak up to the sky and gulp,
    settle. Breathing and a weight shift under it all; the tail and folded wings follow the hips a beat late."""
    W = bird_world(idle, t % idle_len)
    w = window(t, 0.0, 1.0, 4.6, 5.3)                        # the stance (S-neck drawn back), in and out without a pop
    s = strike_curve(t); s_late = strike_curve(t - 0.09)     # the tail answers the strike a beat late
    creep = window(t, 1.0, 2.4, 2.5, 2.75)                  # the neck eases forward over the water while stalking
    coil = window(t, 2.45, 2.72, 2.75, 2.9)                 # the anticipation: drawn back further just before the throw
    scan = math.sin(2 * math.pi * t / 2.3) * 0.14 * window(t, 0.6, 1.2, 2.2, 2.5)
    cock = 0.16 * window(t, 1.4, 1.8, 2.1, 2.45)             # the head tilted to one side, eyeing the water
    shake = math.sin(2 * math.pi * 7 * (t - 2.95)) * 0.13 * window(t, 2.95, 3.0, 3.15, 3.4)
    sky = window(t, 3.8, 4.05, 4.35, 4.7)                    # beak up to swallow
    gulp = math.sin(2 * math.pi * 4 * (t - 3.9)) * 0.07 * window(t, 3.9, 4.0, 4.3, 4.5)
    breathe = math.sin(2 * math.pi * t / 2.4)
    e = {}
    e['hips'] = xrot(0.55 * s + 0.08 * w + 0.04 * creep) @ zrot(0.03 * math.sin(2 * math.pi * t / 1.9))
    e['spine_1'] = xrot(0.015 * breathe)
    e['neck_1'] = xrot(-0.45 * w * (1 - s) + 0.47 * s + 0.12 * creep - 0.14 * coil)
    e['neck_2'] = xrot(0.35 * w * (1 - s) + 0.10 * s - 0.06 * coil - gulp)
    e['neck_3'] = xrot(0.25 * w * (1 - s) - 0.15 * s - 0.08 * creep + 0.05 * coil - 0.38 * sky + 1.6 * gulp) @ yrot(scan + shake)
    e['head'] = xrot(-0.85 * sky) @ zrot(cock)
    e['tail_1'] = xrot(-0.22 * s_late + 0.02 * breathe) @ yrot(-0.5 * shake)
    e['mouth_lower'] = xrot(0.28 * window(t, 2.7, 2.8, 3.3, 3.45) + 0.12 * sky)
    return W, e


BIRD_H = float(bk.world_rest()[bk.index('head')][1, 3])       # the bird standing, to its head (bird units)
HIPS = bk.index('hips')
def lift(mesh, W, base):
    """The bird's own rise and fall (its hips over its idle hips), in this mesh's units, over a base that puts the feet on the ground."""
    k = (STAND_M / mesh.mpu) / BIRD_H
    return base + np.array([0, (W[HIPS][1, 3] - IW[HIPS][1, 3]) * k, 0])
def ground_base(mesh, W):
    P, _ = mesh.skin(mesh.pose(W)); return np.array([0, -P[:, 1].min() / mesh.mpu, 0])

os.makedirs(OUT, exist_ok=True)
def ground_cam(): return render.Camera(np.array([0, 1.0, 0]), 70, 8, 4.6)
def gif(frames, out, cam_fn, fps=30):
    imgs = []
    for label, mesh, posed in frames:
        P, N = mesh.skin(posed)
        img = render.Renderer(mesh.m, cam_fn()).frame(P, N, ground_y=0.0)
        ImageDraw.Draw(img).text((8, 8), label, fill=(40, 40, 40)); imgs.append(img)
    render.save_gif(imgs, os.path.join(OUT, out), fps=fps); print('gif', out, len(imgs))

def feet_on_ground(mesh, posed):
    P, _ = mesh.skin(posed); return np.array([0, -P[:, 1].min() / mesh.mpu, 0])

if 'wade' in want:
    nd, ln = clip('walk'); fr = []
    for k in range(int(ln / 0.6 * 30) * 2):
        posed = stand.pose(highstep(nd, ln, k / 30)); fr.append(('wading walk (slowed, high steps)', stand, stand.pose(highstep(nd, ln, k / 30), root_shift=feet_on_ground(stand, posed))))
    gif(fr, 'stilt2_wade.gif', ground_cam)
    P, _ = stand.skin(stand.pose(IW)); print('standing height %.2f m' % (P[:, 1].max() - P[:, 1].min()))
if 'fishing' in want:
    fr = []
    for k in range(int(HUNT_LEN * 30)):
        W, e = hunt(k / 30); p0 = stand.pose(W, e)
        fr.append(('hunting stance, stab, swallow', stand, stand.pose(W, e, root_shift=feet_on_ground(stand, p0))))
    gif(fr, 'stilt2_fishing.gif', lambda: render.Camera(np.array([0, 0.9, 0.3]), 75, 6, 4.2))
if 'landing' in want or 'flight' in want:
    land_nd, land_ln = clip('land'); LE = bird_world(land_nd, land_ln * 0.999)
    BASE = {id(stand): ground_base(stand, IW), id(fly): ground_base(fly, LE)}
    seq = []
    if 'flight' in want:
        to, tl = clip('take-off')
        for k in range(int(tl * 30)):
            t = tl * k / int(tl * 30); W = bird_world(to, t)
            mesh = stand if t < 0.12 * tl else fly           # the swap: the first beat lifts the folded wings, then the flight mesh
            seq.append(('take-off' + (' (standing mesh)' if mesh is stand else ''), mesh, mesh.pose(W, root_shift=lift(mesh, W, BASE[id(mesh)]))))
        for c in ['flap', 'flap', 'glide']:
            nd, ln = clip(c)
            for k in range(int(ln * 30)):
                W = bird_world(nd, ln * k / int(ln * 30)); seq.append((c, fly, fly.pose(W, root_shift=lift(fly, W, BASE[id(fly)]) + [0, 0.25, 0])))
        gif(seq, 'stilt2_flight.gif', lambda: render.Camera(np.array([0, 1.4, 0]), 35, 14, 7.0))
    if 'landing' in want:
        seq = []; nd, ln = clip('land'); n = int(ln * 30)
        for k in range(n):
            t = ln * k / n; W = bird_world(nd, t)
            mesh = fly if t < 0.9 * ln else stand           # the swap once the feet are down and the wings mostly folded
            seq.append(('landing' + (' -> standing mesh' if mesh is stand else ' (flight mesh)'), mesh, mesh.pose(W, root_shift=lift(mesh, W, BASE[id(mesh)]))))
        for k in range(30):
            W = bird_world(idle, k / 30); seq.append(('standing', stand, stand.pose(W, root_shift=lift(stand, W, BASE[id(stand)]))))
        gif(seq, 'stilt2_landing.gif', lambda: render.Camera(np.array([0, 2.6, 0]), 55, 8, 10.5))
