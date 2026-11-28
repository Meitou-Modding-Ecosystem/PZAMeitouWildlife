# Decodes EXT_meshopt_compression buffer views (mode ATTRIBUTES, filters NONE / OCTAHEDRAL / QUATERNION / EXPONENTIAL; mode TRIANGLES /
# INDICES are refused) into a plain GLB, following the extension's spec (KhronosGroup/glTF, Vendor/EXT_meshopt_compression, Appendix A/B).
# Author: PZA. Local tool for reading Mesh2Motion's CC0 animations.  python meshopt_decode.py in.glb out.glb
import json, math, struct, sys


def unzig(v):
    return (~(v >> 1)) & 0xFF if v & 1 else v >> 1


def decode_attributes(data, count, stride):
    if not data or data[0] != 0xA0:
        raise ValueError('unsupported attribute stream header 0x%02x' % (data[0] if data else -1))
    tail = max(stride, 32)
    if len(data) < 1 + tail:
        raise ValueError('stream too short')
    last = bytearray(data[len(data) - stride:])   # the baseline element
    out = bytearray(count * stride)
    pos, done = 1, 0
    max_block = min((8192 // stride) & ~15, 256)
    end = len(data) - tail
    while done < count:
        n = min(count - done, max_block)
        groups = (n + 15) // 16
        for k in range(stride):
            hdr_bytes = (groups + 3) // 4
            hdr = data[pos:pos + hdr_bytes]
            pos += hdr_bytes
            deltas = []
            for g in range(groups):
                mode = (hdr[g // 4] >> ((g % 4) * 2)) & 3
                if mode == 0:
                    deltas += [0] * 16
                elif mode == 3:
                    deltas += list(data[pos:pos + 16]); pos += 16
                else:
                    bits = 2 if mode == 1 else 4
                    nbytes = 16 * bits // 8
                    packed = data[pos:pos + nbytes]; pos += nbytes
                    vals = []
                    per = 8 // bits
                    mask = (1 << bits) - 1
                    for byte in packed:
                        for s in range(per):
                            vals.append((byte >> (8 - bits * (s + 1))) & mask)   # most significant first
                    for v in vals:
                        if v == mask:
                            deltas.append(data[pos]); pos += 1
                        else:
                            deltas.append(v)
            prev = last[k]
            for i in range(n):
                prev = (prev + unzig(deltas[i])) & 0xFF
                out[(done + i) * stride + k] = prev
            last[k] = prev
        done += n
        if pos > end:
            raise ValueError('stream overrun')
    return bytes(out)


def filter_quaternion(buf, count):
    out = bytearray(len(buf))
    r = 1.0 / math.sqrt(2.0)
    for i in range(count):
        a, b, c, d = struct.unpack_from('<4h', buf, i * 8)
        one = d | 3
        x, y, z = a / one * r, b / one * r, c / one * r
        w = math.sqrt(max(0.0, 1.0 - x * x - y * y - z * z))
        mc = d & 3
        o = [0] * 4
        o[(mc + 1) % 4] = round(x * 32767); o[(mc + 2) % 4] = round(y * 32767); o[(mc + 3) % 4] = round(z * 32767); o[mc % 4] = round(w * 32767)
        struct.pack_into('<4h', out, i * 8, *o)
    return bytes(out)


def filter_exponential(buf):
    out = bytearray(len(buf))
    for i in range(len(buf) // 4):
        v = struct.unpack_from('<i', buf, 4 * i)[0]
        e = v >> 24
        m = ((v << 8) & 0xFFFFFFFF)
        m = (m - (1 << 32) if m & 0x80000000 else m) >> 8
        struct.pack_into('<f', out, 4 * i, math.ldexp(float(m), e))
    return bytes(out)


def filter_octahedral(buf, count, stride):
    out = bytearray(len(buf))
    fmt, mx = ('<4b', 127) if stride == 4 else ('<4h', 32767)
    for i in range(count):
        a = struct.unpack_from(fmt, buf, i * stride)
        one = float(a[2]) or 1.0
        x, y = a[0] / one, a[1] / one
        z = 1.0 - abs(x) - abs(y)
        t = min(z, 0.0)
        x -= math.copysign(t, x); y -= math.copysign(t, y)
        l = math.sqrt(x * x + y * y + z * z) or 1.0
        struct.pack_into(fmt, out, i * stride, round(x / l * mx), round(y / l * mx), round(z / l * mx), a[3])
    return bytes(out)


def main(src, dst):
    b = open(src, 'rb').read()
    jl = struct.unpack('<I', b[12:16])[0]
    j = json.loads(b[20:20 + jl])
    off = 20 + jl
    binc = b''
    if off < len(b):
        cl, ct = struct.unpack('<II', b[off:off + 8])
        binc = b[off + 8:off + 8 + cl]
    out = bytearray()
    for bv in j['bufferViews']:
        ext = bv.get('extensions', {}).get('EXT_meshopt_compression')
        if ext:
            if ext.get('buffer', 0) != 0:
                raise ValueError('compressed data outside the GLB chunk')
            raw = binc[ext.get('byteOffset', 0):ext.get('byteOffset', 0) + ext['byteLength']]
            if ext.get('mode') != 'ATTRIBUTES':
                raise ValueError('mode ' + str(ext.get('mode')) + ' not supported')
            data = decode_attributes(raw, ext['count'], ext['byteStride'])
            f = ext.get('filter', 'NONE')
            if f == 'QUATERNION': data = filter_quaternion(data, ext['count'])
            elif f == 'EXPONENTIAL': data = filter_exponential(data)
            elif f == 'OCTAHEDRAL': data = filter_octahedral(data, ext['count'], ext['byteStride'])
            stride = ext['byteStride']
            del bv['extensions']
            if bv.get('byteStride') is None and stride not in (0,) and any(a.get('bufferView') == j['bufferViews'].index(bv) for a in j.get('accessors', [])):
                pass
        else:
            data = binc[bv.get('byteOffset', 0):bv.get('byteOffset', 0) + bv['byteLength']]
        while len(out) % 4: out.append(0)
        bv['buffer'] = 0
        bv['byteOffset'] = len(out)
        bv['byteLength'] = len(data)
        out += data
    while len(out) % 4: out.append(0)
    j['buffers'] = [{'byteLength': len(out)}]
    for key in ('extensionsUsed', 'extensionsRequired'):
        if key in j:
            j[key] = [e for e in j[key] if e != 'EXT_meshopt_compression']
            if not j[key]: del j[key]
    js = json.dumps(j, separators=(',', ':')).encode()
    while len(js) % 4: js += b' '
    total = 12 + 8 + len(js) + 8 + len(out)
    with open(dst, 'wb') as f:
        f.write(struct.pack('<III', 0x46546C67, 2, total))
        f.write(struct.pack('<II', len(js), 0x4E4F534A)); f.write(js)
        f.write(struct.pack('<II', len(out), 0x004E4942)); f.write(out)


if __name__ == '__main__':
    main(sys.argv[1], sys.argv[2])
