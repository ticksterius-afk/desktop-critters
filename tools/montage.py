"""Contact-sheet a few preview frames into one PNG.

Only has to handle the output of our own PngWriter, which always emits
filter-type 0 scanlines, so "decoding" is just dropping one byte per row.
"""
import sys, zlib, struct, os

def read_png(path):
    data = open(path, 'rb').read()
    assert data[:8] == b'\x89PNG\r\n\x1a\n', path
    pos, idat, w, h = 8, b'', 0, 0
    while pos < len(data):
        (ln,) = struct.unpack('>I', data[pos:pos+4])
        typ = data[pos+4:pos+8]
        chunk = data[pos+8:pos+8+ln]
        if typ == b'IHDR':
            w, h, bd, ct = struct.unpack('>IIBB', chunk[:10])
            assert bd == 8 and ct == 6, (bd, ct)
        elif typ == b'IDAT':
            idat += chunk
        pos += 12 + ln
    raw = zlib.decompress(idat)
    stride = w * 4
    rows = [raw[y*(stride+1)+1 : y*(stride+1)+1+stride] for y in range(h)]
    return w, h, rows

def write_png(path, w, h, rows):
    raw = b''.join(b'\x00' + bytes(r) for r in rows)
    def chunk(t, d):
        c = struct.pack('>I', len(d)) + t + d
        return c + struct.pack('>I', zlib.crc32(t + d) & 0xffffffff)
    out = b'\x89PNG\r\n\x1a\n'
    out += chunk(b'IHDR', struct.pack('>IIBBBBB', w, h, 8, 6, 0, 0, 0))
    out += chunk(b'IDAT', zlib.compress(raw, 6))
    out += chunk(b'IEND', b'')
    open(path, 'wb').write(out)

def half(w, h, rows):
    """2x2 box downsample."""
    hw, hh = w // 2, h // 2
    out = []
    for y in range(hh):
        r0, r1 = rows[2*y], rows[2*y+1]
        row = bytearray(hw * 4)
        for x in range(hw):
            for c in range(4):
                a = r0[(2*x)*4+c] + r0[(2*x+1)*4+c] + r1[(2*x)*4+c] + r1[(2*x+1)*4+c]
                row[x*4+c] = a >> 2
        out.append(row)
    return hw, hh, out

def main():
    frames = sys.argv[2:]
    cols = int(os.environ.get('COLS', '3'))
    tiles = []
    for f in frames:
        w, h, rows = read_png(f)
        tiles.append(half(w, h, rows))

    tw, th = tiles[0][0], tiles[0][1]
    rowsN = (len(tiles) + cols - 1) // cols
    W, H = tw * cols, th * rowsN
    canvas = [bytearray(W * 4) for _ in range(H)]
    for i, (w, h, rows) in enumerate(tiles):
        ox, oy = (i % cols) * tw, (i // cols) * th
        for y in range(h):
            canvas[oy+y][ox*4 : ox*4 + w*4] = rows[y]

    write_png(sys.argv[1], W, H, canvas)
    print(f'{sys.argv[1]} {W}x{H} from {len(tiles)} frames')


if __name__ == "__main__":
    main()
