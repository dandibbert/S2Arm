#!/usr/bin/env python3
"""单一 IMG v1（pack/img.py + services/romfs_index --std）单测：python3 tests/img_test.py。期望值来自夹具本身，不跑被测实现推：
  ① 头：魔数 / 格式号 1 / 游戏号大写 / 资源包号 / 文件长 = 实际长度
  ② 其余文件：按路径 O(1) 查得到、起点页对齐、字节与源文件相同；查不在的路径 → None；路径重复 → 报红
  ③ RomFS 段：按 Switch 标准 RomFS 公开格式独立解析（本文件 std_romfs，不用 LibHac）——文件集合与夹具目录相同、逐文件字节相同
  ④ write：游戏号取产物根 unpack.tsv；资源包号 = 清单里除着色器库 / 覆盖层以外各行的 SHA-256（测试里按定义独立算）；exefs / ctl / aoc 进其余文件段，
     romfs*、覆盖层、直连库、清单不进；RomFS 段来自产物根完整 romfs/（含被覆盖层替换的原文件）；有 programs/ → 报红；is_img 认魔数
  ⑤ convert（出包交付）：资源包目录只剩 game.img + 着色器库 / 覆盖层 + 新清单；index.tsv 首行 game.img<TAB>文件长<TAB>资源包号、其余是原着色器库 / 覆盖层行；
     manifest v3（format img、imgFile、index_sha256 = 新 index.tsv 的 SHA-256）；着色器库那行变了资源包号不变；多程序合集 → None、目录不动
  ⑥ 原件 + 预建偏移表（11-11 真机对比：产物根 romfs 是界面覆盖后版本、有覆盖层时起局重建目录表 138.6 ms）：RomFS 段 = 产物根 romfs，覆盖清单里的文件换回 seed.txt 数据目录的原版
     （数据目录里别的文件变过也不取它）；
     game.imgmap 按 romfs.s2aidx 顺序，被覆盖的记 -1、其余从 game.img 读回的字节等于文件本身；index.tsv 登记 romfs.s2aidx 与 game.imgmap；残留的 romfs.files/ 删掉；
     没有 seed.txt 但产物根叠过覆盖 → 报红
     （要 dotnet 编 services/romfs_index；没有 dotnet 时 ③ 跳过并打印原因，①② 照测）"""
import hashlib, json, os, shlex, shutil, struct, subprocess, sys, tempfile
from pathlib import Path

R = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(R / 'pack'))
import img

bad = 0
def ck(ok, msg):
    global bad
    print(('通 ' if ok else '不通 ') + msg); bad += 0 if ok else 1


def std_romfs(path, base):
    """按公开格式读标准 RomFS → {相对路径: 字节}"""
    f = open(path, 'rb')
    def rd(off, n): f.seek(base + off); return f.read(n)
    hs, dho, dhs, dmo, dms, fho, fhs, fmo, fms, data = struct.unpack('<10Q', rd(0, 0x50))
    dmeta, fmeta, out = rd(dmo, dms), rd(fmo, fms), {}
    def walk(off, path):
        _, _, child, first, _, nl = struct.unpack_from('<6I', dmeta, off)
        name = dmeta[off + 24: off + 24 + nl].decode(); p = path + ('/' + name if name else '')
        fo = first
        while fo != 0xFFFFFFFF:
            _, sib, foff, size, _, fnl = struct.unpack_from('<IIQQII', fmeta, fo)
            out[(p + '/' + fmeta[fo + 32: fo + 32 + fnl].decode()).lstrip('/')] = rd(data + foff, size); fo = sib
        c = child
        while c != 0xFFFFFFFF: walk(c, p); c = struct.unpack_from('<6I', dmeta, c)[1]
    walk(0, ''); return out


def romfs_index_cmd(t):
    dotnet = shutil.which('dotnet') or (os.path.join(os.environ['DOTNET_ROOT'], 'dotnet') if os.environ.get('DOTNET_ROOT') else None)
    if not dotnet: return None, '没有 dotnet'
    out = os.path.join(t, 'ri')
    r = subprocess.run([dotnet, 'build', str(R / 'services/romfs_index'), '-c', 'Release', '-o', out], capture_output=True, text=True)
    if r.returncode: return None, '编 services/romfs_index 失败：' + r.stdout[-300:]
    return f'{shlex.quote(dotnet)} {shlex.quote(os.path.join(out, "romfs_index.dll"))}', ''


with tempfile.TemporaryDirectory(prefix='img test ') as t:
    T = Path(t); (T / 'romfs/a/b c').mkdir(parents=True); (T / 'o').mkdir()
    files = {'a/b c/big.bin': os.urandom(100000), 'a/小.txt': b'hi', 'empty': b''}
    for k, v in files.items(): (T / 'romfs' / k).write_bytes(v)
    others = {'exefs/main.npdm': os.urandom(5000), 'ctl/control.nacp': b'NACP'}
    src = []
    for k, v in others.items(): p = T / 'o' / k.replace('/', '_'); p.write_bytes(v); src.append((k, str(p)))
    cmd, why = romfs_index_cmd(t)
    out = str(T / 'g.img')
    img.build(str(T / 'romfs') if cmd else None, src, out, '0100abcd00000000', 'abc123', cmd, say=lambda *_: None)
    h = img.header(out)
    ck((h['format'], h['gid'], h['pack_id'], h['total']) == (1, '0100ABCD00000000', 'abc123', os.path.getsize(out)), f'① 头 {h}')
    for k, v in others.items():
        off, size = img.find(out, k); b = open(out, 'rb'); b.seek(off)
        ck(off % img.PAGE == 0 and b.read(size) == v, f'② {k} 页对齐、字节相同（偏移 {off}）')
    ck(img.find(out, 'nope') is None, '② 查不在的路径 → None')
    try: img.toc_bytes([('x', 0, 1), ('x', 1, 1)]); ck(False, '② 路径重复应报红')
    except img.Red: ck(True, '② 路径重复报红')
    if cmd:
        got = std_romfs(out, h['romfs'][0])
        ck(got == files, f'③ 标准 RomFS 段：独立解析 {len(got)} 个文件，集合与字节同夹具')
    else:
        print(f'跳过 ③：{why}')
    if cmd:
        root, pk = T / 'root', T / 'pack'
        (root / 'romfs/ui').mkdir(parents=True); (root / 'romfs/ui/layout.bin').write_bytes(b'ORIG'); (root / 'romfs/data.bin').write_bytes(b'DATA')
        (root / 'unpack.tsv').write_text('title_id\t0100ABCD00000001\n')
        rows = {'exefs/main.npdm': b'NPDM', 'ctl/control.nacp': b'NACP', 'aoc/1/x.bin': b'AOC', 'romfs_overlay/ui/layout.bin': b'OVER',
                'shaders/direct/keys.tsv': b'k', 'romfs.files/data.bin': b'DATA', 'romfs.imgmap': b'm'}
        for k, v in rows.items(): (pk / k).parent.mkdir(parents=True, exist_ok=True); (pk / k).write_bytes(v)
        (pk / 'index.tsv').write_text(''.join(f'{k}\t{len(v)}\tx\n' for k, v in rows.items()))
        (pk / 'manifest.json').write_text('{"version": 2, "index_sha256": "feedbeef"}')
        want_id = hashlib.sha256(''.join(f'{k}\t{len(v)}\tx\n' for k, v in rows.items() if not k.startswith(('shaders/', 'romfs_overlay/'))).encode()).hexdigest()
        w = str(T / 'w.img'); img.write(str(root), str(pk), w, romfs_index=cmd)
        hw = img.header(w); names = [e[0] for e in img.entries(w)]
        ck((hw['gid'], hw['pack_id'], img.is_img(w), img.is_img(str(pk / 'index.tsv'))) == ('0100ABCD00000001', want_id, True, False), f'④ 头 {hw["gid"]} / {hw["pack_id"]}、is_img')
        ck(names == ['aoc/1/x.bin', 'ctl/control.nacp', 'exefs/main.npdm', 'romfs.list'], f'④ 其余文件段只收原始资源：{names}')
        ck(std_romfs(w, hw['romfs'][0]) == {'ui/layout.bin': b'ORIG', 'data.bin': b'DATA'}, '④ RomFS 段来自产物根完整 romfs/（含被覆盖层替换的原文件 ORIG）')
        ix0 = (pk / 'index.tsv').read_text()
        (pk / 'index.tsv').write_text(ix0.replace('shaders/direct/keys.tsv\t1\tx', 'shaders/direct/keys.tsv\t1\ty'))
        ck(img.image_id(str(pk)) == want_id, '⑤ 着色器库那行变了，资源包号不变')
        (pk / 'index.tsv').write_text(ix0)
        cv = T / 'cv'; shutil.copytree(pk, cv)
        g = img.convert(str(root), str(cv), romfs_index=cmd)
        left = sorted(str(p.relative_to(cv)) for p in cv.rglob('*') if p.is_file())
        ck(left == ['game.img', 'index.tsv', 'manifest.json', 'romfs_overlay/ui/layout.bin', 'shaders/direct/keys.tsv'], f'⑤ 资源包只剩 IMG + 着色器库 / 覆盖层 + 清单：{left}')
        ix = (cv / 'index.tsv').read_text(); m = json.loads((cv / 'manifest.json').read_text())
        ck(ix == f'game.img\t{os.path.getsize(g)}\t{want_id}\nromfs_overlay/ui/layout.bin\t4\tx\nshaders/direct/keys.tsv\t1\tx\n', f'⑤ 新 index.tsv：{ix!r}')
        ck((m['version'], m['format'], m['imgFile'], m['index_sha256']) == (3, 'img', 'game.img', hashlib.sha256(ix.encode()).hexdigest()), f'⑤ manifest v3：{m}')
        ck(std_romfs(g, img.header(g)['romfs'][0]) == {'ui/layout.bin': b'ORIG', 'data.bin': b'DATA'} and img.find(g, 'exefs/main.npdm') is not None, '⑤ game.img 内容同 write')
        # ⑥ 原件 + 预建偏移表
        orig = T / 'orig'; (orig / 'romfs/ui').mkdir(parents=True); (orig / 'romfs/ui/layout.bin').write_bytes(b'ORIGINAL'); (orig / 'romfs/data.bin').write_bytes(b'STALE')   # 数据目录播种后变过（非覆盖文件）：应取产物根那份
        r6 = T / 'root6'; (r6 / 'romfs/ui').mkdir(parents=True); (r6 / 'romfs/ui/layout.bin').write_bytes(b'OVER'); (r6 / 'romfs/data.bin').write_bytes(b'DATA')
        (r6 / 'seed.txt').write_text(f'src={orig}\ntag=t\n'); (r6 / 'ui_overlay.tsv').write_text('ui/layout.bin\n'); (r6 / 'unpack.tsv').write_text('title_id\t0100ABCD00000006\n')
        r = subprocess.run([*shlex.split(cmd), str(r6)], capture_output=True, text=True); ck(r.returncode == 0, f'⑥ 造预建索引（romfs_index）rc={r.returncode} {r.stderr[-200:]}')
        p6 = T / 'pack6'; (p6 / 'exefs').mkdir(parents=True); (p6 / 'romfs_overlay/ui').mkdir(parents=True); (p6 / 'romfs.files').mkdir()
        (p6 / 'exefs/main.npdm').write_bytes(b'NPDM'); (p6 / 'romfs_overlay/ui/layout.bin').write_bytes(b'OVER'); (p6 / 'romfs.files/data.bin').write_bytes(b'DATA')
        shutil.copyfile(r6 / 'romfs.s2aidx', p6 / 'romfs.s2aidx')
        r6rows = {'exefs/main.npdm': b'NPDM', 'romfs_overlay/ui/layout.bin': b'OVER', 'romfs.s2aidx': (p6 / 'romfs.s2aidx').read_bytes()}
        (p6 / 'index.tsv').write_text(''.join(f'{k}\t{len(v)}\t{hashlib.sha256(v).hexdigest()}\n' for k, v in r6rows.items()))
        (p6 / 'manifest.json').write_text('{"version": 2}')
        g6 = img.convert(str(r6), str(p6), romfs_index=cmd)
        ck(std_romfs(g6, img.header(g6)['romfs'][0]) == {'ui/layout.bin': b'ORIGINAL', 'data.bin': b'DATA'}, '⑥ RomFS 段是原件（被覆盖的 ui/layout.bin 是原版字节）')
        sys.path.insert(0, str(R / 'pack')); import romfs_image
        _, idx6 = romfs_image.parse_index(str(p6 / 'romfs.s2aidx'))
        mb = (p6 / 'game.imgmap').read_bytes(); n6 = struct.unpack_from('<i', mb, 16)[0]; offs = struct.unpack_from(f'<{n6}q', mb, 20)
        def at(o, n): f = open(g6, 'rb'); f.seek(o); return f.read(n)
        got6 = {rel: (-1 if o < 0 else at(o, n)) for (_, n, rel), o in zip(idx6, offs)}
        ck(mb[:8] == b'S2ARIM01' and struct.unpack_from('<q', mb, 8)[0] == os.path.getsize(g6) and got6 == {'ui/layout.bin': -1, 'data.bin': b'DATA'},
           f'⑥ game.imgmap：镜像长 = game.img、被覆盖的 -1、其余读回原字节 {got6}')
        ix6 = (p6 / 'index.tsv').read_text()
        ck('romfs.s2aidx\t' in ix6 and 'game.imgmap\t' in ix6 and not (p6 / 'romfs.files').exists(), '⑥ index.tsv 登记 romfs.s2aidx / game.imgmap，romfs.files/ 已删')
        (r6 / 'seed.txt').unlink()
        try: img.orig_romfs(str(r6), str(T)); ck(False, '⑥ 没有 seed.txt 但叠过覆盖应报红')
        except img.Red: ck(True, '⑥ 没有 seed.txt 但产物根叠过覆盖 → 报红')
        (pk / 'programs/1').mkdir(parents=True); (pk / 'programs/1/a').write_bytes(b'p')
        (pk / 'index.tsv').write_text((pk / 'index.tsv').read_text() + 'programs/1/a\t1\tx\n')
        try: img.write(str(root), str(pk), w, romfs_index=cmd); ck(False, '④ 多程序合集应报红')
        except img.Red: ck(True, '④ 有 programs/ → 报红（这一版不出 IMG）')
        before = sorted(p.name for p in pk.rglob('*'))
        ck(img.convert(str(root), str(pk), romfs_index=cmd) is None and sorted(p.name for p in pk.rglob('*')) == before, '⑤ 多程序合集 → None、目录不动')
print(f'img 单测：{"全通" if not bad else f"{bad} 项不通"}'); sys.exit(1 if bad else 0)
