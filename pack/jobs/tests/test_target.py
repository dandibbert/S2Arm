"""目标设备探测（pack/jobs/target.py）单测（从 test_error_recovery.py 拆出，10-26；夹具 jobcase.JobCase）。"""
import subprocess
import unittest
from unittest.mock import patch

from jobcase import JobCase
from core import Failure
import target


class TargetTests(JobCase):
    def test_connected_keeps_two_devices_with_same_arch_and_os(self):
        import diag
        self.c.r["developerDir"] = str(self.root / "synthetic-developer")
        # 两台实体设备恰好同架构 / 系统，不能按编译目标去重丢掉设备身份。
        devices = [
            dict(identifier=identity,
                 hardwareProperties=dict(reality="physical", platform="iOS",
                                         productType="iPhone18,2", udid=identity + "-udid"),
                 deviceProperties=dict(name=identity, osVersionNumber="27.1"),
                 connectionProperties=dict(tunnelState="connected"))
            for identity in ("synthetic-phone-a", "synthetic-phone-b")
        ]
        with patch.object(diag, "devicectl", return_value=(0, {"result": {"devices": devices}}, "")) as probe, \
             patch.object(target, "gpu_arch", return_value="applegpu_g18p"):
            rows = target.connected(self.c)
        probe.assert_called_once_with(self.c, "list", "devices", timeout=60)
        self.assertEqual([row["id"] for row in rows], ["synthetic-phone-a", "synthetic-phone-b"])
        self.assertEqual([row["udid"] for row in rows],
                         ["synthetic-phone-a-udid", "synthetic-phone-b-udid"])
        self.assertEqual([(row["gpuArch"], row["os"]) for row in rows],
                         [("applegpu_g18p", 27), ("applegpu_g18p", 27)])

    def test_connected_excludes_other_platforms_but_keeps_ios_and_ipados(self):
        import diag
        self.c.r["developerDir"] = str(self.root / "synthetic-developer")
        # 所有条目均为 physical 且已连通，拒绝项只能由平台判据排除。
        platforms = ("iOS", "iPadOS", "macOS", "tvOS", "watchOS", "")
        devices = [
            dict(identifier="synthetic-" + platform,
                 hardwareProperties=dict(reality="physical", platform=platform,
                                         productType="synthetic-model"),
                 deviceProperties=dict(name=platform, osVersionNumber="27.1"),
                 connectionProperties=dict(tunnelState="connected"))
            for platform in platforms
        ]
        with patch.object(diag, "devicectl", return_value=(0, {"result": {"devices": devices}}, "")) as probe, \
             patch.object(target, "gpu_arch", return_value="applegpu_g18p") as arch:
            rows = target.connected(self.c)
        probe.assert_called_once_with(self.c, "list", "devices", timeout=60)
        self.assertEqual([row["id"] for row in rows], ["synthetic-iOS", "synthetic-iPadOS"])
        self.assertEqual(arch.call_count, 2)

    def test_target_probe_preserves_error_code(self):
        with patch.object(target, "device_list", return_value=([], "", None, None)), \
             patch.object(target, "models", side_effect=target.TargetError("missing database", code="xcode_unavailable")):
            with self.assertRaises(Failure) as raised:
                target.probe(self.c)
        self.assertEqual(raised.exception.code, "xcode_unavailable")

    def test_target_probe_quick_skips_device_list(self):
        # quick：只给机型 / 系统静态表，不读连着的设备（出包页立刻能选机型；10-26 维护者：机型表等设备探测 61 秒）
        fixture = [dict(name="fixture", productType="iPhone18,2", gpuArch="applegpu_g18p")]
        self.c.r["quick"] = True
        with patch.object(target, "device_list", side_effect=AssertionError("quick 不该读设备")), \
             patch.object(target, "models", return_value=fixture), patch.object(target, "os_choices", return_value=[27, 26]):
            self.assertEqual(target.probe(self.c), dict(connected=[], connectedError="", models=fixture, osChoices=[27, 26]))   # quick 没读设备：不是读失败
        self.c.r.pop("quick")
        with patch.object(target, "device_list", return_value=(["raw"], "", None, None)) as seen, \
             patch.object(target, "_keep_device", lambda d: True), patch.object(target, "_row", lambda d, dev_dir, link: "dev"), \
             patch.object(target, "models", return_value=fixture), patch.object(target, "os_choices", return_value=[27]):
            self.assertEqual(target.probe(self.c)["connected"], ["dev"])   # 完整探测读设备列表（打桩，不碰真手机）
        seen.assert_called_once()

    def test_target_probe_survives_device_list_timeout(self):
        # devicectl 超时只丢「连着的设备」，可选机型与系统照常给（10-26 Spare-host：超时冲掉整个探测，机型 0 种）
        import diag
        fixture = [dict(name="fixture", productType="iPhone18,2", gpuArch="applegpu_g18p")]
        with patch.object(diag, "devicectl", side_effect=subprocess.TimeoutExpired(["xcrun", "devicectl"], 60)), \
             patch.object(target, "models", return_value=fixture), patch.object(target, "os_choices", return_value=[27, 26]):
            result = target.probe(self.c)
        self.assertEqual(result, dict(connected=[], connectedError="读取设备列表超时：解锁手机，重新插一次数据线再试。", models=fixture, osChoices=[27, 26]))   # 读失败要说出来，不当「没连」

    def test_min_os_is_18_metal3_range_accepted(self):
        # 期望（10-26 维护者：直连下限 iOS 18 / macOS 15，18–25 走 Metal 3 兼容路径）：A14（applegpu_g13p）+ iOS 18 能出包；iOS 17 拒、提示写「iOS 18」；A13（g12）拒
        ok = target.check(dict(gpuArch="applegpu_g13p", os=18, model="iPhone 12"))
        self.assertEqual(ok["os"], 18)
        with self.assertRaises(target.TargetError) as e:
            target.check(dict(gpuArch="applegpu_g13p", os=17, model="iPhone 12"))
        self.assertIn("iOS 18", str(e.exception))
        with self.assertRaises(target.TargetError):
            target.check(dict(gpuArch="applegpu_g12p", os=18, model="iPhone 11"))

    def test_os_choices_span_sdk_down_to_18(self):
        # 期望：所用 iOS SDK 27.1 → 可选系统 27、26 … 18 共 10 个（手算）
        with patch.object(target.subprocess, "run", return_value=subprocess.CompletedProcess([], 0, "27.1\n", "")):
            self.assertEqual(target.os_choices({}), list(range(27, 17, -1)))


if __name__ == "__main__":
    unittest.main()
