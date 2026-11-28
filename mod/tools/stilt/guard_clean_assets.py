# PZA's Wildlife for Meitou: re-encodes the Mire Stilt's binary assets until the release guard is CLEAN on them. Author: PZA. Licence: GPLv3
# or later. Compressed bytes can happen to spell a guarded word; the same content packed at another deflate level / strategy (the .pzaf
# bakes) or another PNG compression level gives other bytes. The content never changes: the bakes decompress to the same body, the
# PNGs decode to the same pixels.
#   python guard_clean_assets.py <assets\stilt folder> <release_guard.py>
import os, sys, zlib, shutil, subprocess, tempfile, io
from PIL import Image

folder, guard = sys.argv[1], sys.argv[2]

def clean(name, data):
    d = tempfile.mkdtemp(prefix='pza_guard_')
    try:
        with open(os.path.join(d, name), 'wb') as f: f.write(data)
        r = subprocess.run([sys.executable, '-I', guard, d], capture_output=True, text=True, encoding='utf-8', errors='replace')
        return r.returncode == 0
    finally:
        shutil.rmtree(d, ignore_errors=True)

def pzaf(path):
    raw = open(path, 'rb').read()
    body = zlib.decompress(raw[8:], -15)
    if clean(os.path.basename(path), raw): return 'already clean'
    for level in (9, 8, 7, 6):
        for mem in (9, 8, 7):
            for strategy in (zlib.Z_DEFAULT_STRATEGY, zlib.Z_FILTERED):
                co = zlib.compressobj(level, zlib.DEFLATED, -15, mem, strategy)
                data = raw[:8] + co.compress(body) + co.flush()
                assert zlib.decompress(data[8:], -15) == body
                if clean(os.path.basename(path), data):
                    open(path, 'wb').write(data); return f'level {level} mem {mem} strategy {strategy}'
    return 'NOT CLEAN'

def png(path):
    raw = open(path, 'rb').read()
    if clean(os.path.basename(path), raw): return 'already clean'
    im = Image.open(path); im.load(); px = im.tobytes()
    for level in range(9, -1, -1):
        for optimize in (True, False):
            b = io.BytesIO(); im.save(b, 'PNG', compress_level=level, optimize=optimize); data = b.getvalue()
            assert Image.open(io.BytesIO(data)).tobytes() == px
            if clean(os.path.basename(path), data):
                open(path, 'wb').write(data); return f'compress_level {level} optimize {optimize}'
    return 'NOT CLEAN'

for f in ('stilt_ground.pzaf', 'stilt_air.pzaf'): print(f, pzaf(os.path.join(folder, f)))
for f in ('ground.png', 'air.png'): print(f, png(os.path.join(folder, 'textures', f)))
