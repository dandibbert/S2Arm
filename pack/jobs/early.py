"""出包与装机重叠（11-06 出包速度线，维护者：玩家都是冷出包，等的是「出包开始 → 能玩」）。

「打 iPhone 包」请求带 installAfter（打完装到连着的这台）时，ipa.py 用本模块在出包的同时先装 App、先推大头资源：
  ① 开工前认账号与设备（install.pick_team / phone）：缺 Xcode 账号或没连手机，出包前就报，不等十几分钟后才发现
  ② 刷新旁路（pack/refresh.sh 快路：改写 / 系统数据 / 能力表 / RomFS 索引）完成后写 S2A_EARLY_READY（现行版本名）
  ③ 本线程见到它 → pack/early_pack.sh 出早期资源包（不含着色器库）+ 组装带这款的早期容器（ipa.assemble，不动游戏库里那份）→ install.sign_and_install 签名装 App
     → install.push_pack 推早期资源包并在机上解开（机上 index.applied.tsv 记下已有文件）；与直连库编译并行
  ④ 出包结束 → finish() 等本线程收尾 → ipa.py 照常把正式版放进容器装机（install.put_game）：push_pack 按机上已解开的清单只补差量（着色器库 + 变了的小文件）
早装出错不判整单失败（正式安装会推整包兜底），只记一条进度消息；用到的命令行日志都写本模块自己的文件（不经 Context.run，免得改主线程的 last_log）。
"""
from __future__ import annotations

import os
import subprocess
import threading
import time
from pathlib import Path

class Overlap:
    """出包与装机重叠的共用骨架（Mac 启动器 EarlyInstall、Windows pack/orch/oneclick_win.py --install 同用；11-06 两端合一，原先两边各写一份线程与兜底）：
    守护线程里 先 wait(stop)（等旁路信号；返回 False = 出包已结束 / 失败，不早装）→ build() 出早装件 → install(件) 签名装 App + 推早期资源包（tools/push_pack.py）。
    早装是提速不是交付：任何失败只记下（error），正式安装照常推整包兜底。finish() 置停、等收尾（在推的推完）→ (结果, 错误)；结果带用时 seconds"""

    def __init__(self, build, install, wait=None):
        self.build, self.install, self.wait = build, install, wait
        self.stop = threading.Event()
        self.result, self.error = {}, None
        self.thread = threading.Thread(target=self._run, name="early-install", daemon=True)

    def start(self):
        self.thread.start()
        return self

    def _run(self):
        try:
            if self.wait and not self.wait(self.stop):
                return
            t0 = time.time()
            self.result = dict(self.install(self.build()) or {}, seconds=round(time.time() - t0))
        except Exception as e:   # 早装是提速，不是交付：任何失败都留给正式安装兜底
            self.error = e

    def finish(self):
        self.stop.set()
        self.thread.join()
        return self.result, self.error


class EarlyInstall:
    """Mac 启动器（ipa.py installAfter）：旁路写 S2A_EARLY_READY → 早期资源包 + 早期容器 → install.MacPhone（Xcode 账号 + devicectl + push_pack）"""

    def __init__(self, c, gid, env, out):
        import install as inst
        self.c, self.gid, self.inst, self.env = c, gid, inst, env
        self.team = inst.devcert.team(c) or inst.pick_team(inst.xcode_teams(env), c.r.get("teamId"))
        self.dev = inst.phone(c)
        self.udid = inst.udid_of(c, self.dev["id"])
        self.bid = inst.container_bundle_id(self.team["id"], c)
        self.ready = c.logs / (c.task + "-early.ready")
        self.ready.unlink(missing_ok=True)
        self.log = c.logs / (c.task + "-early.log")
        self.dir = Path(out) / ".early"
        env["S2A_EARLY_READY"] = str(self.ready)
        self.core = Overlap(self._build, self._install, self._wait)
        self.stop = self.core.stop

    def start(self):
        self.core.start()
        return self

    def _wait(self, stop):
        while not self.ready.is_file():
            if stop.wait(0.5):
                return False   # 出包结束 / 失败时旁路还没写信号：不早装
        return True

    def _build(self):
        kuan = self.ready.read_text(encoding="utf-8").strip().split("@")[0]
        with open(self.log, "w", encoding="utf-8") as f:
            rc = subprocess.run(["bash", str(self.c.engine / "pack/early_pack.sh"), kuan, str(self.dir)],
                                stdout=f, stderr=subprocess.STDOUT, env=self.env, cwd=str(self.c.engine)).returncode
        if rc:
            from core import Failure
            raise Failure("早装件没出成：" + _last(self.log), code="job_failed")
        import ipa as packer
        import layout
        root = layout.ios_work(self.c, self.gid) / "roots" / (kuan + "@现行")
        rec = layout.load_device(self.c, self.udid or self.dev["id"])   # 这台手机上已有的各款一起带上（早装覆盖装也不能把它们装没）
        others = [g for g in rec.get("games", {}) if g != self.gid.upper()]
        return packer.assemble(self.c, self.gid, others, failed=rec.get("failedShells", []), root=root.resolve(), dest=self.dir / "container")

    def _install(self, built):
        ipa, games, _ = built
        ph = self.inst.MacPhone(self.c, self.team, self.dev, self.udid)
        ph.install(ipa, games, steps=False)
        files, size = ph.push(self.gid.upper(), self.dir / "pack")
        return dict(files=files, bytes=size)

    def finish(self):
        """出包结束后调：等早装收尾（在推的推完），返回 {files, bytes, seconds}；出错只记进度消息"""
        result, error = self.core.finish()
        if error is not None:
            # 玩家不用做任何事（正式安装推整包兜底）：只记一条进度消息，不新增错误码
            self.c.emit("message", "出包期间先装 App、先推资源没成（" + str(error)[:200] + "），装正式包时整包推送")
        return result


def _last(log):
    try:
        lines = [l for l in Path(log).read_text(encoding="utf-8", errors="replace").splitlines() if l.strip()]
    except OSError:
        return str(log)
    return (lines[-1] if lines else str(log))[:200]
