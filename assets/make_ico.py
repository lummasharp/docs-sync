# Packs icon-*.png into DocsSync.ico (PNG-compressed entries, Vista+).
import struct, pathlib

here = pathlib.Path(__file__).parent
sizes = [16, 24, 32, 48, 64, 128, 256]
pngs = [(here / f"icon-{n}.png").read_bytes() for n in sizes]

header = struct.pack("<HHH", 0, 1, len(sizes))
offset = 6 + 16 * len(sizes)
entries, blobs = b"", b""
for n, data in zip(sizes, pngs):
    dim = 0 if n == 256 else n  # 0 means 256 in ICO
    entries += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(data), offset)
    blobs += data
    offset += len(data)

(here / "DocsSync.ico").write_bytes(header + entries + blobs)
print("wrote DocsSync.ico", 6 + len(entries) + len(blobs), "bytes")
