// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 S2Arm contributors
#include "ns2_controller.h"
#include "../runtime/include/s2a_pad.h"
#include "ns2_relay.h"
#include "../ios/haptic_retry.h"
#include <errno.h>
#include <math.h>
#include <pthread.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <sys/un.h>
#include <unistd.h>

static pthread_mutex_t lock = PTHREAD_MUTEX_INITIALIZER;
static pthread_mutex_t relayWriteLock = PTHREAD_MUTEX_INITIALIZER;
static ns2_state input;
static ns2_motion relayMotion;
static ns2_kind relayKind;   // 通道 v2 带来的对外输出型号（持 lock）
static ns2_status status;
static int directStarted, ready, foreground, motion_requested;
static int relayFD = -1;
static int relayLastError;   // 上次连通道失败的 errno（只在主线程连接路径读写）
static pthread_t relayThread;
static int relayThreadStarted;
static uint64_t epoch = 1;
static uint16_t back_buttons[2];
static float rumble_scale = 1;

static float rumble_amplitude(float amplitude) {
    return fminf(1, s2a_hp_feel(amplitude) * rumble_scale);
}

static void receive_input(const ns2_state *state, void *context) {
    pthread_mutex_lock(&lock);
    if (ready && foreground) input = *state;
    pthread_mutex_unlock(&lock);
}

static void receive_status(ns2_status next, const char *message, void *context) {
    pthread_mutex_lock(&lock);
    status = next;
    int connected = next == NS2_STATUS_READY;
    if (ready != connected) {
        epoch++;
        memset(&input, 0, sizeof input);
    }
    ready = connected;
    pthread_mutex_unlock(&lock);
    fprintf(stderr, "[Mac NS2] status=%d %s\n", next, message);
}

// Supplying a motion callback selects report05 in the existing transport.
static void receive_motion(const ns2_motion *motion, void *context) {
    (void)context;
    (void)motion;
}

static int connect_relay(const char *path) {
    struct sockaddr_un address = {0};
    size_t length = strlen(path);
    if (length == 0 || length >= sizeof address.sun_path) return 0;

    int fd = socket(AF_UNIX, SOCK_STREAM, 0);
    if (fd < 0) return 0;
#ifdef SO_NOSIGPIPE
    int one = 1;
    setsockopt(fd, SOL_SOCKET, SO_NOSIGPIPE, &one, sizeof one);
#endif
    address.sun_family = AF_UNIX;
    memcpy(address.sun_path, path, length + 1);
    if (connect(fd, (struct sockaddr *)&address, sizeof address)) {
        int error = errno;
        close(fd);
        // 开局时手柄还没连、游戏每秒重试：同一个原因只记一次，接上后再失败重新记
        if (error != relayLastError) fprintf(stderr, "[Mac NS2] Relay unavailable at %s: %s\n", path, strerror(error));
        relayLastError = error;
        return 0;
    }
    relayLastError = 0;

    pthread_mutex_lock(&lock);
    relayFD = fd;
    ready = 0;
    status = NS2_STATUS_CONNECTING;
    memset(&input, 0, sizeof input);
    memset(&relayMotion, 0, sizeof relayMotion);
    relayKind = NS2_KIND_NONE;
    pthread_mutex_unlock(&lock);
    return 1;
}

static int receive_all(int fd, void *buffer, size_t length) {
    char *cursor = buffer;
    while (length) {
        ssize_t received = recv(fd, cursor, length, 0);
        if (received <= 0) {
            if (received < 0 && errno == EINTR) continue;
            return 0;
        }
        cursor += received;
        length -= (size_t)received;
    }
    return 1;
}

static void *relay_read_main(void *context) {
    int fd = (int)(intptr_t)context;
    ns2_relay_packet packet;
    while (receive_all(fd, &packet, sizeof packet)) {
        if (packet.magic != NS2_RELAY_MAGIC || packet.version != NS2_RELAY_VERSION || packet.type != NS2_RELAY_INPUT) {
            if (packet.magic == NS2_RELAY_MAGIC && packet.version != NS2_RELAY_VERSION)
                fprintf(stderr, "[Mac NS2] 启动器通道版本 %u，本端 %u：断开（启动器与游戏宿主版本不一致）\n", packet.version, NS2_RELAY_VERSION);
            break;
        }
        pthread_mutex_lock(&lock);
        if (relayFD != fd) {
            pthread_mutex_unlock(&lock);
            break;
        }
        int connected = packet.ready != 0;
        if (ready != connected) {
            epoch++;
            memset(&input, 0, sizeof input);
        }
        ready = connected;
        status = (ns2_status)packet.status;
        if (connected) input = packet.state;
        relayKind = connected ? (ns2_kind)packet.kind : NS2_KIND_NONE;
        if (packet.motion_known) relayMotion = packet.motion;
        else memset(&relayMotion, 0, sizeof relayMotion);
        pthread_mutex_unlock(&lock);
    }

    pthread_mutex_lock(&lock);
    if (relayFD == fd) {
        relayFD = -1;
        ready = 0;
        status = NS2_STATUS_DISCONNECTED;
        epoch++;
        memset(&input, 0, sizeof input);
        memset(&relayMotion, 0, sizeof relayMotion);
        relayKind = NS2_KIND_NONE;
    }
    pthread_mutex_unlock(&lock);
    close(fd);
    fprintf(stderr, "[Mac NS2] Relay connection closed\n");
    return NULL;
}

static int relay_send(const ns2_relay_packet *packet) {
    pthread_mutex_lock(&relayWriteLock);
    pthread_mutex_lock(&lock);
    int fd = relayFD;
    pthread_mutex_unlock(&lock);
    ssize_t sent = fd < 0 ? -1 : send(fd, packet, sizeof *packet, 0);
    pthread_mutex_unlock(&relayWriteLock);
    return sent == (ssize_t)sizeof *packet;
}

static int relay_command(uint16_t type) {
    ns2_relay_packet packet = {0};
    packet.magic = NS2_RELAY_MAGIC;
    packet.version = NS2_RELAY_VERSION;
    packet.type = type;
    return relay_send(&packet);
}

static void set_relay_motion(int enabled) {
    ns2_relay_packet packet = {0};
    packet.magic = NS2_RELAY_MAGIC;
    packet.version = NS2_RELAY_VERSION;
    packet.type = NS2_RELAY_MOTION;
    packet.enabled = enabled;
    relay_send(&packet);
}

int s2a_mac_ns2_connect(void) {
    const char *scale = getenv("S2A_RUMBLE_SCALE");
    float value = scale ? strtof(scale, NULL) : 1;
    rumble_scale = isfinite(value) && value > 0 ? value : 1;
    pthread_mutex_lock(&lock);
    if (relayFD >= 0) {
        pthread_mutex_unlock(&lock);
        return 1;
    }
    if (directStarted) {
        pthread_mutex_unlock(&lock);
        return ns2_rescan();
    }
    pthread_mutex_unlock(&lock);

    const char *relayPath = getenv("S2A_NS2_SOCKET");
    if (relayPath) {
        if (!connect_relay(relayPath)) return 0;
        if (relayThreadStarted) {
            pthread_join(relayThread, NULL);
            relayThreadStarted = 0;
        }
        pthread_mutex_lock(&lock);
        int fd = relayFD;
        pthread_mutex_unlock(&lock);
        if (pthread_create(&relayThread, NULL, relay_read_main, (void *)(intptr_t)fd)) {
            pthread_mutex_lock(&lock);
            relayFD = -1;
            pthread_mutex_unlock(&lock);
            close(fd);
            return 0;
        }
        relayThreadStarted = 1;
        return 1;
    }

    int started = ns2_start_with_motion(receive_input, receive_status, receive_motion, NULL);
    if (started) {
        pthread_mutex_lock(&lock);
        directStarted = 1;
        ns2_set_motion_enabled(motion_requested);
        pthread_mutex_unlock(&lock);
    }
    return started;
}

void s2a_mac_ns2_detach(void) {
    pthread_mutex_lock(&lock);
    int fd = relayFD;
    int threadStarted = relayThreadStarted;
    int direct = directStarted;
    relayFD = -1;
    relayThreadStarted = 0;
    directStarted = 0;
    ready = 0;
    status = NS2_STATUS_STOPPED;
    epoch++;
    memset(&input, 0, sizeof input);
    memset(&relayMotion, 0, sizeof relayMotion);
    relayKind = NS2_KIND_NONE;
    pthread_mutex_unlock(&lock);
    if (fd >= 0) shutdown(fd, SHUT_RDWR);
    if (threadStarted) pthread_join(relayThread, NULL);
    if (direct) ns2_shutdown("Mac game closed", 200);
}

void s2a_mac_ns2_disconnect(void) {
    pthread_mutex_lock(&lock);
    int relay = relayFD >= 0;
    pthread_mutex_unlock(&lock);
    if (relay) relay_command(NS2_RELAY_DISCONNECT);
    s2a_mac_ns2_detach();
}

void s2a_mac_ns2_focus(int focused) {
    pthread_mutex_lock(&lock);
    int changed = foreground != !!focused;
    if (changed) {
        foreground = !!focused;
        epoch++;
        memset(&input, 0, sizeof input);
    }
    int relay = relayFD >= 0;
    int motion = motion_requested && foreground;
    pthread_mutex_unlock(&lock);
    if (changed) {
        if (relay) set_relay_motion(motion);
        else ns2_set_motion_enabled(motion);
        if (!foreground) {
            if (relay) relay_command(NS2_RELAY_STOP_RUMBLE);
            else ns2_stop_rumble();
        }
    }
}

int s2a_mac_ns2_read(ns2_state *state) {
    pthread_mutex_lock(&lock);
    int available = ready && foreground;
    *state = available ? input : (ns2_state){0};
    if (state->buttons & (1ull << 19)) state->buttons |= back_buttons[0];
    if (state->buttons & (1ull << 20)) state->buttons |= back_buttons[1];
    pthread_mutex_unlock(&lock);
    return available;
}

void s2a_mac_ns2_set_back_buttons(uint16_t left, uint16_t right) {
    pthread_mutex_lock(&lock);
    back_buttons[0] = left;
    back_buttons[1] = right;
    pthread_mutex_unlock(&lock);
}

int s2a_mac_ns2_relay_due(ns2_status status, const char *relay) {
    return relay && *relay && status == NS2_STATUS_IDLE && access(relay, F_OK) == 0;
}

ns2_kind s2a_mac_ns2_kind(void) {
    pthread_mutex_lock(&lock);
    ns2_kind kind = !ready ? NS2_KIND_NONE : relayFD >= 0 ? relayKind : ns2_get_output_kind();
    pthread_mutex_unlock(&lock);
    return kind;
}

const char *s2a_mac_ns2_kind_name(ns2_kind kind) {
    switch (kind) {
        case NS2_KIND_JOYCON_L: return "Joy-Con 2 (L)";
        case NS2_KIND_JOYCON_R: return "Joy-Con 2 (R)";
        case NS2_KIND_JOYCON_PAIR: return "Joy-Con 2 (L + R)";
        case NS2_KIND_PRO: return "Switch 2 Pro";
        default: return "Switch 手柄";   // 没连 / 还不知道型号（不分代）
    }
}

ns2_status s2a_mac_ns2_status(void) {
    pthread_mutex_lock(&lock);
    ns2_status value = status;
    pthread_mutex_unlock(&lock);
    return value;
}

#define EXPORT __attribute__((visibility("default"), used))
uint64_t s2a_mac_ns2_rumble_session(void) {
    pthread_mutex_lock(&lock);
    uint64_t session = ready && foreground ? epoch : 0;
    pthread_mutex_unlock(&lock);
    return session;
}

void s2a_mac_ns2_set_rumble_pair(uint64_t session, const s2a_pad_vibration values[2]) {
    ns2_vibration pair[2] = {
        {rumble_amplitude(values[0].low_amp), values[0].low_hz, rumble_amplitude(values[0].high_amp), values[0].high_hz},
        {rumble_amplitude(values[1].low_amp), values[1].low_hz, rumble_amplitude(values[1].high_amp), values[1].high_hz}
    };
    pthread_mutex_lock(&lock);
    int accepted = ready && foreground && session == epoch;
    int relay = relayFD >= 0;
    pthread_mutex_unlock(&lock);
    if (!accepted) return;
    if (relay) {
        ns2_relay_packet packet = {0};
        packet.magic = NS2_RELAY_MAGIC;
        packet.version = NS2_RELAY_VERSION;
        packet.type = NS2_RELAY_RUMBLE;
        packet.duration_ms = 1000;
        memcpy(packet.vibration, pair, sizeof pair);
        relay_send(&packet);
    } else {
        ns2_send_pair_lease(pair, 1000);
    }
}

void s2a_mac_ns2_stop_rumble(uint64_t session) {
    pthread_mutex_lock(&lock);
    int accepted = session == epoch;
    int relay = relayFD >= 0;
    pthread_mutex_unlock(&lock);
    if (!accepted) return;
    if (relay) relay_command(NS2_RELAY_STOP_RUMBLE);
    else ns2_stop_rumble();
}

EXPORT void s2a_mac_ns2_set_motion_enabled(int enabled) {
    pthread_mutex_lock(&lock);
    motion_requested = !!enabled;
    int relay = relayFD >= 0;
    int active = motion_requested && foreground;
    pthread_mutex_unlock(&lock);
    if (relay) {
        set_relay_motion(active);
    } else {
        ns2_set_motion_enabled(active);
    }
}

EXPORT int s2a_mac_ns2_read_motion(s2a_pad_motion *out) {
    ns2_motion sample = {0};
    pthread_mutex_lock(&lock);
    if (ready && foreground && motion_requested) {
        if (relayFD >= 0) sample = relayMotion;
        else ns2_get_motion(&sample);
    }
    pthread_mutex_unlock(&lock);
    *out = (s2a_pad_motion){0};
    if (!sample.known) return 0;
    *out = (s2a_pad_motion){sample.host_timestamp_us,
        sample.acceleration_x, sample.acceleration_y, sample.acceleration_z,
        sample.gyro_x, sample.gyro_y, sample.gyro_z};
    return 1;
}
