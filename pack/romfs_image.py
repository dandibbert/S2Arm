#!/usr/bin/env python3
"""把资源包的 romfs/ 换成单文件镜像：romfs_image.py <按文件的资源包> <输出资源包> --root <产物根>
镜像 romfs.img 按 romfs.s2aidx 的文件顺序，把除界面覆盖层外的文件首尾相接放在一起；romfs.imgmap 记索引里每个文件在镜像里的偏移。
界面覆盖层（产物根 ui_overlay.tsv 列的文件）不进镜像，放 romfs_overlay/ 按文件推：覆盖层变了不用重推镜像。
程序合集的 programs/<n>/romfs 同样处理。其余文件在输出目录里硬链接原文件。
romfs.imgmap（小端）："S2ARIM01" | i64 镜像长 | i32 文件数 | 文件数 × i64 偏移（-1 = 在覆盖层），顺序同 romfs.s2aidx 的文件表。
#329（同一款的数据在盘上只占一份）：库里的资源包不存 romfs.img——macOS 没有区间克隆，几万个文件拼成的大文件和逐文件那一族（解包、
产物根、Mac 版、pack.files）共享不了块，存着就是第 2 份。改存 romfs.files/<相对路径>：镜像里每个文件的硬链接（同卷零额外占用，NTFS 同样），
index.tsv 照旧记 romfs.img 的长度与 sha256（按镜像顺序读一遍算出）。推送时要推镜像才由 materialize 按 romfs.s2aidx + romfs.imgmap 现拼、
边拼边核长度与 sha256，推完删（tools/push_pack.py）。"""
import hashlib, json, os, random, shutil, struct, sys
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'tools'))
import sha256_fast

MAGIC = b'S2ARIM01'
CHUNK = 1 << 22
FILES = 'romfs.files'   # 资源包里镜像各文件的硬链接目录（不进 index.tsv、不推手机）
VERSION = 'romfs-image-v2'   # v2：不存 romfs.img、存 romfs.files 硬链接（#329）


class Red(Exception):
    pass


def parse_index(path):
    b = open(path, 'rb').read()
    if b[:8] != b'S2ARFS01': raise Red(f'RomFS 索引格式不对：{path}')
    total, nf = struct.unpack_from('<qi', b, 8); p = 20
    rows = [struct.unpack_from('<qqii', b, p + 24 * i) for i in range(nf)]; p += 24 * nf
    p += 4
    return total, [(off, n, b[p + po:p + po + pl].decode('utf-8')) for off, n, po, pl in rows]


def overlay_names(root):
    try:
        with open(os.path.join(root, 'ui_overlay.tsv'), encoding='utf-8') as f: return {l.strip() for l in f if l.strip()}
    except FileNotFoundError:
        return set()


def link(src, dst):
    os.makedirs(os.path.dirname(dst), exist_ok=True)
    try: os.link(os.path.realpath(src), dst)
    except OSError: shutil.copy2(src, dst)


def prefixes(rows):
    return sorted({r[0][:r[0].index('romfs/')] for r in rows if r[0].startswith('romfs/') or (r[0].startswith('programs/') and '/romfs/' in r[0])})


def write_image(src_dir, tmp, prefix, rows, overlay):
    """→ 新索引行。romfs 文件必须和索引一一对应，对不上就是索引过期，不出半份镜像"""
    have = {r[0][len(prefix) + 6:]: r for r in rows if r[0].startswith(prefix + 'romfs/')}
    idx_path = os.path.join(src_dir, prefix + 'romfs.s2aidx')
    if not os.path.isfile(idx_path): raise Red(f'{prefix}romfs 没有索引 romfs.s2aidx')
    _, files = parse_index(idx_path)
    listed = {rel for _, n, rel in files}
    zero = {rel for rel, r in have.items() if int(r[1]) == 0}
    if set(have) - zero != listed: 
        extra, lost = sorted(set(have) - zero - listed), sorted(listed - set(have))
        raise Red(f'{prefix}romfs 文件与索引对不上：多 {len(extra)} 个（如 {extra[:3]}），缺 {len(lost)} 个（如 {lost[:3]}）；索引过期，重出 RomFS 索引')
    os.makedirs(os.path.join(tmp, prefix, FILES), exist_ok=True)   # 空 RomFS（#578：数据全在 exefs）也要有这个目录：交付 / 推送按它认「库内形态」，没有就退去找不存在的 romfs.img
    h, pos, offsets, out = sha256_fast.new(), 0, [], []
    for _, n, rel in files:   # 不写镜像：按镜像顺序读一遍算长度与 sha256，文件硬链进 romfs.files/（推送时现拼）
        if int(have[rel][1]) != n: raise Red(f'{prefix}romfs/{rel} 大小与索引不符')
        if rel in overlay: offsets.append(-1); continue
        offsets.append(pos)
        src = os.path.join(src_dir, prefix, 'romfs', rel)
        with open(src, 'rb') as f:
            left = n
            while left:
                b = f.read(min(CHUNK, left))
                if not b: raise Red(f'{prefix}romfs/{rel} 读不满')
                h.update(b); left -= len(b); pos += len(b)
        link(src, os.path.join(tmp, prefix, FILES, rel))
    with open(os.path.join(tmp, prefix + 'romfs.imgmap'), 'wb') as m:
        m.write(MAGIC + struct.pack('<qi', pos, len(offsets))); m.write(struct.pack(f'<{len(offsets)}q', *offsets))
    out.append((prefix + 'romfs.img', pos, h.hexdigest()))
    mp = os.path.join(tmp, prefix + 'romfs.imgmap')
    out.append((prefix + 'romfs.imgmap', os.path.getsize(mp), hashlib.sha256(open(mp, 'rb').read()).hexdigest()))
    for rel in sorted(overlay & set(have)):
        r = have[rel]; link(os.path.join(src_dir, r[0]), os.path.join(tmp, prefix, 'romfs_overlay', rel)); out.append((prefix + 'romfs_overlay/' + rel, int(r[1]), r[2]))
    return out


def build(src_dir, out, root):
    text = open(os.path.join(src_dir, 'index.tsv'), encoding='utf-8').read()
    rows = [l.split('\t') for l in text.splitlines() if l]
    pres = prefixes(rows)
    overlays = {p: overlay_names(os.path.join(root, p)) for p in pres}
    key = hashlib.sha256('\n'.join([VERSION, text, *(f'{p}\t{n}' for p in pres for n in sorted(overlays[p]))]).encode()).hexdigest()
    key_file = out.rstrip('/\\') + '.image.key'
    try:
        same = open(key_file, encoding='utf-8').read().strip() == key and json.load(open(os.path.join(out, 'manifest.json'), encoding='utf-8')).get('version') == 2
    except (OSError, ValueError):
        same = False
    if same:
        print(f'镜像未变，沿用 {out}'); return
    tmp = out.rstrip('/\\') + '.tmp'; shutil.rmtree(tmp, ignore_errors=True); os.makedirs(tmp)
    new = []
    for p in pres: new += write_image(src_dir, tmp, p, rows, overlays[p])
    for r in rows:
        if any(r[0].startswith(p + 'romfs/') for p in pres): continue
        link(os.path.join(src_dir, r[0]), os.path.join(tmp, r[0])); new.append((r[0], int(r[1]), r[2]))
    ix = ''.join(f'{p}\t{n}\t{s}\n' for p, n, s in new).encode('utf-8')
    with open(os.path.join(tmp, 'index.tsv'), 'wb') as f: f.write(ix)
    with open(os.path.join(tmp, 'manifest.json'), 'w', encoding='utf-8', newline='\n') as f:
        json.dump({'version': 2, 'files': len(new), 'total': sum(n for _, n, _ in new), 'index_sha256': hashlib.sha256(ix).hexdigest()}, f, indent=1)
    shutil.rmtree(out, ignore_errors=True); os.replace(tmp, out)
    with open(key_file + '.tmp', 'w', encoding='utf-8', newline='\n') as f: f.write(key + '\n')
    os.replace(key_file + '.tmp', key_file)
    img = sum(n for p, n, _ in new if p.endswith('romfs.img'))
    print(f'🟢 单文件镜像 {img / 1e9:.2f} GB；资源包 {len(rows)} → {len(new)} 个文件，{out}')


def materialize(pack, img_rel, size, want_sha, say=print, progress=None):
    """按 <前缀>romfs.s2aidx 的文件顺序、<前缀>romfs.imgmap 的偏移，把 <前缀>romfs.files/ 里的文件拼成 <前缀>romfs.img（先写 .part 再改名），
    边拼边核：长度、sha256 都要等于 index.tsv 记的（size / want_sha），对不上删掉半份、报红。→ 写出的路径（调用方推完删）"""
    pack = os.fspath(pack); prefix = img_rel[:-len('romfs.img')]
    _, files = parse_index(os.path.join(pack, prefix + 'romfs.s2aidx'))
    mb = open(os.path.join(pack, prefix + 'romfs.imgmap'), 'rb').read()
    if mb[:8] != MAGIC: raise Red(f'{prefix}romfs.imgmap 格式不对')
    total, nf = struct.unpack_from('<qi', mb, 8)
    if nf != len(files) or total != size: raise Red(f'{prefix}romfs.imgmap 与索引对不上（文件 {nf} / {len(files)}，镜像长 {total} / {size}）')
    offsets = struct.unpack_from(f'<{nf}q', mb, 20)
    dst = os.path.join(pack, img_rel); part = dst + '.part'
    h, pos = sha256_fast.new(), 0
    import time; last = time.monotonic()   # progress(已拼字节)：每秒一次（装机页「准备资源（拼镜像）X / Y GB」）
    try:
        with open(part, 'wb') as w:
            for o, (_, n, rel) in zip(offsets, files):
                if o < 0: continue
                if o != pos: raise Red(f'{prefix}romfs.imgmap 偏移不连续：{rel} 记 {o}、应为 {pos}')
                with open(os.path.join(pack, prefix, FILES, rel), 'rb') as f:
                    left = n
                    while left:
                        b = f.read(min(CHUNK, left))
                        if not b: raise Red(f'{prefix}{FILES}/{rel} 读不满')
                        h.update(b); w.write(b); left -= len(b); pos += len(b)
                        if progress and time.monotonic() - last >= 1: progress(pos); last = time.monotonic()
        if pos != size or h.hexdigest() != want_sha:
            raise Red(f'现拼的 {img_rel} 与索引不符（长 {pos} / {size}，sha256 {h.hexdigest()[:12]} / {want_sha[:12]}）：资源包里的文件被改过，重打 iPhone 包')
        os.replace(part, dst)
    except BaseException:
        try: os.unlink(part)
        except OSError: pass
        raise
    say(f'现拼 {img_rel}：{size / 1e9:.2f} GB（推完删）')
    return dst


def main(argv):
    if len(argv) != 4 or argv[2] != '--root': sys.exit(__doc__)
    try: build(argv[0], argv[1], argv[3])
    except Red as e: sys.exit(f'【红】{e}')


if __name__ == '__main__':
    main(sys.argv[1:])
