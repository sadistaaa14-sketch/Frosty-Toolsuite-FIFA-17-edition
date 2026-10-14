"""Reads a Frosty .cache file the way AssetManager does, and reports what the profile holds.

The cache is the profile's asset table: it is what the editor knows without the game running. Two
numbers decide what a folder export can possibly write: how many ebx assets live under a folder, and
how many chunks the profile has at all. A folder whose assets are named nothing but resources, or a
profile holding no chunks, both end with a run that wrote no .chunk file.

Usage:  python cachepeek.py <path to .cache> [name prefix ...]
"""
import struct
import sys


class Reader:
    def __init__(self, data):
        self.data = data
        self.pos = 0

    def bytes(self, count):
        chunk = self.data[self.pos:self.pos + count]
        if len(chunk) != count:
            raise EOFError("ran out of data at %d" % self.pos)
        self.pos += count
        return chunk

    def uint(self):
        return struct.unpack_from("<I", self.bytes(4))[0]

    def int(self):
        return struct.unpack_from("<i", self.bytes(4))[0]

    def ulong(self):
        return struct.unpack_from("<Q", self.bytes(8))[0]

    def long(self):
        return struct.unpack_from("<q", self.bytes(8))[0]

    def boolean(self):
        return self.bytes(1)[0] == 1

    def string(self):
        start = self.pos
        while self.data[self.pos] != 0:
            self.pos += 1
        text = self.data[start:self.pos].decode("latin-1")
        self.pos += 1
        return text

    def guid(self):
        raw = self.bytes(16)
        return "%08x-%04x-%04x-%s-%s" % (
            struct.unpack_from("<I", raw, 0)[0],
            struct.unpack_from("<H", raw, 4)[0],
            struct.unpack_from("<H", raw, 6)[0],
            raw[8:10].hex(), raw[10:].hex())


def skip_extra(reader):
    reader.bytes(20)
    reader.bytes(20)
    reader.long()
    reader.int()
    reader.boolean()
    reader.string()


def read_cache(path):
    data = open(path, "rb").read()
    reader = Reader(data)

    magic = reader.ulong()
    if magic != 0x02005954534F5246:
        raise SystemExit("not a Frosty cache (magic %x)" % magic)
    version = reader.uint()
    profile_hash = reader.int()
    head = reader.uint()
    print("cache: %s" % path)
    print("  version %d, profile hash 0x%08x, head 0x%x" % (version, profile_hash, head))

    super_bundles = [reader.string() for _ in range(reader.int())]
    print("  super bundles: %d" % len(super_bundles))

    bundles = []
    for _ in range(reader.int()):
        bundles.append((reader.string(), reader.int()))
    print("  bundles: %d" % len(bundles))

    ebx = []
    for _ in range(reader.int()):
        entry = {"name": reader.string()}
        reader.bytes(20)
        reader.long()
        reader.long()
        reader.int()
        reader.boolean()
        entry["type"] = reader.string()
        entry["guid"] = reader.guid()
        if reader.boolean():
            skip_extra(reader)
        entry["bundles"] = [reader.int() for _ in range(reader.int())]
        entry["deps"] = [reader.guid() for _ in range(reader.int())]
        ebx.append(entry)
    print("  ebx: %d" % len(ebx))

    res = []
    for _ in range(reader.int()):
        entry = {"name": reader.string()}
        reader.bytes(20)
        reader.long()
        reader.long()
        entry["location"] = reader.int()
        reader.boolean()
        entry["rid"] = reader.ulong()
        entry["type"] = reader.uint()
        entry["meta"] = reader.bytes(reader.int())
        if reader.boolean():
            skip_extra(reader)
        entry["bundles"] = [reader.int() for _ in range(reader.int())]
        res.append(entry)
    print("  res: %d" % len(res))

    chunks = []
    for _ in range(reader.int()):
        entry = {"id": reader.guid()}
        reader.bytes(20)
        entry["size"] = reader.long()
        entry["location"] = reader.int()
        reader.boolean()
        entry["bundled"] = reader.uint()
        reader.uint()
        reader.uint()
        reader.uint()
        reader.uint()
        reader.int()
        reader.int()
        if reader.boolean():
            skip_extra(reader)
        entry["bundles"] = [reader.int() for _ in range(reader.int())]
        chunks.append(entry)
    print("  chunks: %d" % len(chunks))
    print("  bytes read: %d of %d" % (reader.pos, len(data)))

    return ebx, res, chunks


def main():
    ebx, res, chunks = read_cache(sys.argv[1])
    prefixes = sys.argv[2:] or ["sound"]

    for prefix in prefixes:
        match = prefix.lower()
        under_ebx = [e for e in ebx if e["name"].lower().startswith(match)]
        under_res = [e for e in res if e["name"].lower().startswith(match)]
        print()
        print("under %r: %d ebx, %d res" % (prefix, len(under_ebx), len(under_res)))
        types = {}
        for entry in under_ebx:
            types[entry["type"]] = types.get(entry["type"], 0) + 1
        for name, count in sorted(types.items(), key=lambda kv: -kv[1])[:10]:
            print("    ebx type %-34s %d" % (name, count))
        for name in [e["name"] for e in under_ebx[:4]]:
            print("    ebx e.g. %s" % name)
        for name in [e["name"] for e in under_res[:4]]:
            print("    res e.g. %s" % name)

    print()
    print("profile totals: %d ebx, %d res, %d chunks" % (len(ebx), len(res), len(chunks)))
    if chunks:
        bundled = sum(1 for c in chunks if c["bundles"])
        print("  chunks with a bundle: %d" % bundled)
        for entry in chunks[:3]:
            print("    %s  %d bytes, location %d" % (entry["id"], entry["size"], entry["location"]))


main()
