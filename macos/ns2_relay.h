// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 S2Arm contributors
#ifndef S2A_MAC_NS2_RELAY_H
#define S2A_MAC_NS2_RELAY_H

#include "../runtime/ns2pro/include/NS2Transport.h"
#include <stdint.h>

#define NS2_RELAY_MAGIC 0x534E3252u
// 2：加 kind（对外输出型号，ns2_get_output_kind；#520）。两端版本不同时各自记一行日志并断开，不猜字段
#define NS2_RELAY_VERSION 2u

typedef enum {
    NS2_RELAY_INPUT = 1,
    NS2_RELAY_RUMBLE = 2,
    NS2_RELAY_MOTION = 3,
    NS2_RELAY_STOP_RUMBLE = 4,
    NS2_RELAY_DISCONNECT = 5,
} ns2_relay_type;

typedef struct {
    uint32_t magic;
    uint16_t version;
    uint16_t type;
    uint64_t epoch;
    ns2_state state;
    ns2_motion motion;
    uint8_t ready;
    uint8_t status;
    uint8_t motion_known;
    uint8_t enabled;
    uint8_t kind;   // ns2_kind；只在 INPUT 包里有意义
    uint8_t reserved[3];
    ns2_vibration vibration[2];
    uint32_t duration_ms;
    uint32_t reserved2;
} ns2_relay_packet;


#endif
