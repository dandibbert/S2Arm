#!/usr/bin/env python3
# 内测诊断包入库（10-23 维护者：明天内测 D 组游戏泛化 / E 组硬件矩阵，玩家在 App 里一键「导出诊断包」交回来）：
#   tools/beta_intake.py [--inbox 目录]            把收件目录里没处理过的诊断包（任意 *.zip，不看文件名，信息读 manifest.json）解开、按会话出一行进 summary.tsv
#   tools/beta_intake.py [--inbox 目录] --report   「款 × 机型 × 症状」汇总表（症状：崩溃:<签名> / 卡死或被杀 / 没到 MARK / 正常）
# 诊断包格式（schema 1，字段表发用户体验线对齐过）：manifest.json + logs/host*.log + 可选 logs/frames.tsv、metrickit/*.json。
#   manifest 缺字段一律记「缺」不报错；会话的结局 / MARK / 帧数 / 热状态 / 实占峰值都从日志再算一遍（App 填的只作参考），首遇现编走 tools/firstuse_scan.py 同口径。
# 收件目录缺省 ~/.s2arm/run/s2arm/beta_inbox：zip 放顶层；解开到 x/<包名>/；处理过的记 done/<包名>（重跑不重复入库）。
import argparse, json, os, re, subprocess, sys, zipfile, time, collections, math
from datetime import datetime, timezone
T = os.path.dirname(os.path.abspath(__file__)); sys.path.insert(0, T)
from redline import red_lines

COLS = ['入库时刻', '测试者', '组', '款', '机型', 'iOS', '包提交', '会话', '结局', 'MARK', '帧数', '热状态最高', '实占峰值MB', '崩溃签名', '红行', '首遇现编', '玩家备注', '诊断包', '诊断备注']


def g(d, *ks):
    for k in ks:
        if not isinstance(d, dict) or k not in d or d[k] in (None, ''): return '缺'
        d = d[k]
    return str(d)


def crash_sig(h):
    """崩溃签名：宿主 CRASH 行的信号 + pc 符号；有托管异常时取异常类型 + 第一条 at 栈帧（去掉泛型参数）"""
    m = re.search(r'【红】CRASH 信号 (\d+).*?pc (\S+)', h)
    if not m: return ''
    sig = f'信号{m.group(1)} {m.group(2)}'
    e = re.search(r'\n[^\n]*?(System\.\w+(?:\.\w+)*Exception)[^\n]*\n((?:[^\n]*\n){0,4})', h)
    if e:
        at = re.search(r'at ([\w.]+)', e.group(2))
        sig = f'{e.group(1).split(".")[-1]} @ {at.group(1) if at else "?"}'
    return sig


def session(path, strict_firstuse=False):
    with open(path, errors='replace') as stream: h = stream.read()
    hb = re.findall(r'\[心跳\] 帧 (\d+)，热状态 (\d+).*?进程实占 (\d+) MB', h)
    crash = crash_sig(h)
    if crash: end = 'crash'
    elif '优雅退出完成' in h: end = 'exit'
    else: end = 'unknown'   # 没有退出行也没有崩溃行：被系统杀（jetsam / 看门狗）或还在跑，MetricKit 能进一步区分
    mk = re.search(r'S2A 标记 MARK：帧 (\d+)', h)
    try:
        scan = subprocess.run([sys.executable, os.path.join(T, 'firstuse_scan.py'), path], capture_output=True, text=True)
        fu = scan.stdout.split('\t')[0].strip()
        if strict_firstuse and (scan.returncode or not fu.isdigit()): fu = None
        elif not fu: fu = '0'
    except OSError:
        if not strict_firstuse: raise
        fu = None
    return {'结局': end, 'MARK': '是' if mk else '否', '帧数': max((int(f) for f, _, _ in hb), default=0),
            '热状态最高': max((int(k) for _, k, _ in hb), default=''), '实占峰值MB': max((int(m) for _, _, m in hb), default=''),
            '崩溃签名': crash, '红行': len(red_lines(h)), '首遇现编': fu}


def metric_evidence(root, device_timezone):
    evidence, unreadable = [], False
    directory = os.path.join(root, 'metrickit')
    if not os.path.isdir(directory): return evidence, unreadable
    for name in sorted(os.listdir(directory)):
        if not name.endswith('.json'): continue
        try:
            with open(os.path.join(directory, name), encoding='utf-8') as stream: payload = json.load(stream)
            if not isinstance(payload, dict): raise ValueError('invalid payload')
            found = []
            for key in ('crashDiagnostics', 'hangDiagnostics', 'cpuExceptionDiagnostics', 'diskWriteExceptionDiagnostics'):
                if key in payload and payload[key] is not None and not isinstance(payload[key], list): raise ValueError('invalid diagnostics')
            for crash in payload.get('crashDiagnostics') or []:
                if not isinstance(crash, dict): raise ValueError('invalid crash')
                metadata = crash.get('diagnosticMetaData') if isinstance(crash.get('diagnosticMetaData'), dict) else {}
                reason = crash.get('terminationReason', metadata.get('terminationReason'))
                signal = crash.get('signal', metadata.get('signal'))
                if isinstance(reason, str) and re.search(r'watchdog|0x8badf00d', reason, re.I):
                    found.append('被看门狗杀')
                elif isinstance(signal, int) and not isinstance(signal, bool) and 0 < signal < 128:
                    found.append(f'崩溃:{signal}')
            for hang in payload.get('hangDiagnostics') or []:
                if not isinstance(hang, dict): raise ValueError('invalid hang')
                metadata = hang.get('diagnosticMetaData') if isinstance(hang.get('diagnosticMetaData'), dict) else {}
                duration = hang.get('hangDuration', metadata.get('hangDuration'))
                if isinstance(duration, dict):
                    unit = duration.get('unit', 's')
                    duration = duration.get('value')
                    if unit in ('ms', 'milliseconds') and isinstance(duration, (int, float)): duration /= 1000
                    elif unit not in ('s', 'sec', 'seconds'): duration = None
                if isinstance(duration, str):
                    match = re.fullmatch(r'\s*(\d+(?:\.\d+)?)\s*(ms|s|sec|seconds)\s*', duration)
                    duration = float(match.group(1)) / (1000 if match.group(2) == 'ms' else 1) if match else None
                if isinstance(duration, (int, float)) and not isinstance(duration, bool) and math.isfinite(duration) and 0 <= duration <= 86400:
                    found.append(f'挂起 {duration:g} 秒')
            for key, label in (('cpuExceptionDiagnostics', 'CPU'), ('diskWriteExceptionDiagnostics', '磁盘写入')):
                if payload.get(key): found.append(f'资源超限:{label}')
            evidence.append((parse_time(payload.get('timeStampBegin'), device_timezone), parse_time(payload.get('timeStampEnd'), device_timezone), list(dict.fromkeys(found))))
        except (OSError, ValueError, TypeError, UnicodeError):
            unreadable = True
    return evidence, unreadable


def parse_time(value, default_timezone=None):
    if not isinstance(value, str): return None
    try:
        parsed = datetime.fromisoformat(value.replace('Z', '+00:00'))
        if not parsed.tzinfo and default_timezone: parsed = parsed.replace(tzinfo=default_timezone)
        return parsed.astimezone(timezone.utc) if parsed.tzinfo else None
    except (ValueError, OverflowError): return None


def manifest_timezone(man):
    if not isinstance(man, dict): return None
    def offset(value):
        if not isinstance(value, str): return None
        try:
            parsed = datetime.fromisoformat(value.replace('Z', '+00:00'))
            return parsed.utcoffset() if parsed.tzinfo else None
        except (ValueError, OverflowError): return None
    exported = offset(man.get('exported_at'))
    if exported is not None: return timezone(exported)
    sessions = man.get('sessions')
    if not isinstance(sessions, list): return None
    offsets = {offset(entry.get('started')) for entry in sessions if isinstance(entry, dict)} - {None}
    return timezone(next(iter(offsets))) if len(offsets) == 1 else None


def session_intervals(man, logs, root):
    entries = man.get('sessions') if isinstance(man, dict) else None
    if not isinstance(entries, list): return {}
    starts = {}
    for entry in entries:
        if not isinstance(entry, dict) or not isinstance(entry.get('log'), str): return {}
        name = entry['log']
        started = parse_time(entry.get('started'))
        if name in starts or not started: return {}
        starts[name] = (started, parse_time(entry.get('ended')))
    if len({start for start, _ in starts.values()}) != len(starts): return {}
    ordered = sorted(starts.items(), key=lambda item: item[1][0])
    intervals = {}
    for index, (name, (start, end)) in enumerate(ordered):
        next_start = ordered[index + 1][1][0] if index + 1 < len(ordered) else None
        if end and (end <= start or (next_start and end > next_start)): return {}
        end = end or next_start
        path = os.path.join(root, name)
        if path in logs: intervals[path] = (start, end)
    return intervals if set(intervals) == set(logs) else {}


def metric_result(result, evidence, unreadable, path, intervals):
    notes = []
    if unreadable: notes.append('MetricKit 读不了')
    matched, uncertain = [], []
    for begin, end, items in evidence:
        hits = [log for log, (start, stop) in intervals.items() if begin and end and
                ((begin == end and start <= begin and (stop is None or begin < stop)) or
                 (begin < end and (stop is None or begin < stop) and start < end))]
        if len(hits) == 1 and intervals[hits[0]][1] and hits[0] == path:
            matched.extend(items)
        elif len(hits) != 1 or not intervals[hits[0]][1]:
            uncertain.extend(items)
    matched, uncertain = list(dict.fromkeys(matched)), list(dict.fromkeys(uncertain))
    if matched:
        notes.append('MetricKit（按时间段关联）: ' + '、'.join(matched))
        if len(matched) == 1 and result.get('结局') == 'unknown': result['结局'] = matched[0]
        elif result.get('结局') == 'exit' or (result.get('结局') == 'crash' and any(not item.startswith('崩溃:') for item in matched)):
            notes.append('日志结局与 MetricKit 证据需核对')
    if uncertain: notes.append('MetricKit（诊断包时间段，未确认对应本会话）: ' + '、'.join(uncertain))
    return '；'.join(notes)


def intake(inbox):
    os.makedirs(os.path.join(inbox, 'done'), exist_ok=True); os.makedirs(os.path.join(inbox, 'x'), exist_ok=True)
    out = os.path.join(inbox, 'summary.tsv'); new = not os.path.exists(out); n = 0
    columns = COLS
    if not new:
        with open(out, encoding='utf-8') as existing: columns = existing.readline().rstrip('\n').split('\t')
    with open(out, 'a') as f:
        if new: f.write('\t'.join(columns) + '\n')
        for z in sorted(os.listdir(inbox)):
            if not z.endswith('.zip'): continue
            name = z[:-4]; done = os.path.join(inbox, 'done', name)
            if os.path.exists(done): continue
            dst = os.path.join(inbox, 'x', name)
            try:
                with zipfile.ZipFile(os.path.join(inbox, z)) as zf:
                    for i in zf.infolist():
                        # 没带 UTF-8 标记的文件名 zipfile 按 cp437 解（10-23 App 首版导出的包：中文名成乱码）→ 按 UTF-8 重解，解不了保持原样
                        m = i.filename
                        if not i.flag_bits & 0x800:
                            try: m = m.encode('cp437').decode('utf-8')
                            except (UnicodeEncodeError, UnicodeDecodeError): pass
                        if m.startswith('/') or '..' in m.split('/'): raise ValueError(f'可疑路径 {m}')   # 不信任压缩包里的路径：拒绝绝对路径和 ..
                        if m.endswith('/'): continue
                        os.makedirs(os.path.dirname(os.path.join(dst, m)), exist_ok=True)
                        with zf.open(i) as a, open(os.path.join(dst, m), 'wb') as b: b.write(a.read())
            except Exception as e:
                print(f'🔴 {z} 解不开：{e}'); open(done, 'w').write(f'拒收：{e}\n'); continue   # 拒收也记，重跑不再报
            # manifest.json 在根目录（schema 1）；App 首版把全部文件套在一层同名文件夹里 → 根目录没有就进唯一的顶级文件夹
            if not os.path.exists(os.path.join(dst, 'manifest.json')):
                subs = [d for d in os.listdir(dst) if os.path.isdir(os.path.join(dst, d))]
                if len(subs) == 1 and os.path.exists(os.path.join(dst, subs[0], 'manifest.json')): dst = os.path.join(dst, subs[0])
            try: man = json.load(open(os.path.join(dst, 'manifest.json'), errors='replace'))
            except Exception: man = {}
            logs = sorted(p for p in (os.path.join(r, x) for r, _, fs in os.walk(os.path.join(dst, 'logs')) for x in fs) if re.search(r'host(\.prev\d*)?\.log$', p))
            if not logs: print(f'【黄】{z}：没有 host*.log，只记 manifest')
            evidence, unreadable = metric_evidence(dst, manifest_timezone(man))
            intervals = session_intervals(man, logs, dst)
            for p in logs or [None]:
                s = session(p) if p else {}
                note = metric_result(s, evidence, unreadable, p, intervals)
                row = {'入库时刻': time.strftime('%m-%d %H:%M'), '测试者': g(man, 'tester', 'id'), '组': g(man, 'tester', 'group'), '款': g(man, 'game', 'name'),
                       '机型': g(man, 'device', 'model'), 'iOS': g(man, 'device', 'ios'), '包提交': g(man, 'app', 'commit')[:9], '会话': os.path.basename(p) if p else '无',
                       '玩家备注': g(man, 'tester', 'note').replace('\t', ' ').replace('\n', ' '), '诊断包': z, '诊断备注': note, **s}
                if note and '诊断备注' not in columns: row['玩家备注'] += '；' + note
                f.write('\t'.join(str(row.get(c, '')) for c in columns) + '\n'); n += 1
            open(done, 'w').write(time.strftime('%m-%d %H:%M:%S') + '\n')
            print(f'入库 {z}：{len(logs)} 个会话')
    print(f'本次入库 {n} 行 → {out}')


def report(inbox):
    p = os.path.join(inbox, 'summary.tsv')
    if not os.path.exists(p): print('还没有入库数据'); return
    with open(p) as stream:
        columns = stream.readline().rstrip('\n').split('\t')
        rows = [dict(zip(columns, line.rstrip('\n').split('\t'))) for line in stream if line.strip()]
    c = collections.Counter()
    for r in rows:
        sym = f'崩溃:{r["崩溃签名"]}' if r['结局'] == 'crash' else ('卡死或被杀' if r['结局'] == 'unknown' else (r['结局'] if r['结局'] != 'exit' else ('没到 MARK' if r['MARK'] == '否' else '正常')))
        c[(r['款'], r['机型'], sym)] += 1
    print('款\t机型\t症状\t会话数')
    for (k, m, s), n in sorted(c.items(), key=lambda x: (x[0][0], -x[1])): print(f'{k}\t{m}\t{s}\t{n}')


if __name__ == '__main__':
    a = argparse.ArgumentParser(); a.add_argument('--inbox', default=os.path.expanduser('~/.s2arm/run/s2arm/beta_inbox')); a.add_argument('--report', action='store_true')
    a.add_argument('--diagnostic-report', metavar='INPUT', help='独立安全报告模式：目录或 ZIP，不走 legacy 入库')
    a.add_argument('--output'); a.add_argument('--metadata')
    o = a.parse_args()
    if o.diagnostic_report is not None:
        if not o.output or o.report: a.error('独立报告模式需要 --output，不能与 --report 同用')
        from diagnostic_report import main as diagnostic_main
        sys.exit(diagnostic_main([o.diagnostic_report, '--output', o.output] + (['--metadata', o.metadata] if o.metadata is not None else [])))
    if o.output is not None or o.metadata is not None:
        a.error('--output/--metadata 仅用于 --diagnostic-report 模式')
    os.makedirs(o.inbox, exist_ok=True)
    report(o.inbox) if o.report else intake(o.inbox)
