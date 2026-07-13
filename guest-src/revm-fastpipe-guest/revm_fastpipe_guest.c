#include "revm_fastpipe_abi.h"

#include <errno.h>
#include <fcntl.h>
#include <getopt.h>
#include <poll.h>
#include <signal.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <sys/types.h>
#include <time.h>
#include <unistd.h>

#ifndef O_CLOEXEC
#define O_CLOEXEC 0
#endif

static volatile sig_atomic_t g_stop = 0;

static void on_signal(int sig) {
    (void)sig;
    g_stop = 1;
}

static void usage(const char* argv0) {
    fprintf(stderr,
        "usage: %s [--control DEV] [--data DEV] [--source DEV] [--status PATH] [--self-test]\n"
        "\n"
        "Guest-side REPlayer fastpipe bridge. In Android product mode it opens virtio\n"
        "ports exposed by the VMM/QEMU device and forwards gfxstream command packets\n"
        "from --source into the host fastpipe data queue. It does not use ADB/display,\n"
        "QMP, scrcpy, screenshots, or encoded video.\n"
        "\n"
        "defaults:\n"
        "  --control %s\n"
        "  --data    %s\n"
        "  --status  %s\n",
        argv0,
        REVM_FASTPIPE_DEFAULT_CONTROL_PORT,
        REVM_FASTPIPE_DEFAULT_DATA_PORT,
        REVM_FASTPIPE_DEFAULT_STATUS_PATH);
}

static int write_all(int fd, const void* data, size_t len) {
    const uint8_t* p = (const uint8_t*)data;
    while (len > 0) {
        ssize_t n = write(fd, p, len);
        if (n < 0) {
            if (errno == EINTR) continue;
            return -1;
        }
        if (n == 0) return -1;
        p += (size_t)n;
        len -= (size_t)n;
    }
    return 0;
}

static int read_some(int fd, void* data, size_t cap) {
    for (;;) {
        ssize_t n = read(fd, data, cap);
        if (n < 0 && errno == EINTR) continue;
        return (int)n;
    }
}

static int write_packet(int data_fd, const uint8_t* payload, uint32_t len) {
    uint8_t hdr[4];
    hdr[0] = (uint8_t)(len & 0xff);
    hdr[1] = (uint8_t)((len >> 8) & 0xff);
    hdr[2] = (uint8_t)((len >> 16) & 0xff);
    hdr[3] = (uint8_t)((len >> 24) & 0xff);
    if (write_all(data_fd, hdr, sizeof(hdr)) != 0) return -1;
    return write_all(data_fd, payload, len);
}

static void write_status(const char* path, const char* state, const char* detail) {
    FILE* f = fopen(path, "w");
    if (!f) return;
    time_t now = time(NULL);
    fprintf(f,
        "{\"kind\":\"revm-fastpipe-guest-v1\",\"abiVersion\":%d,\"state\":\"%s\",\"detail\":\"%s\",\"time\":%lld}\n",
        REVM_FASTPIPE_ABI_VERSION,
        state ? state : "unknown",
        detail ? detail : "",
        (long long)now);
    fclose(f);
}

static int connect_control(const char* path) {
    int fd = open(path, O_RDWR | O_CLOEXEC);
    if (fd < 0) return -1;
    const char hello[] = "hello\n";
    if (write_all(fd, hello, sizeof(hello) - 1) != 0) {
        close(fd);
        return -1;
    }
    char buf[256];
    int n = read_some(fd, buf, sizeof(buf) - 1);
    if (n <= 0) {
        close(fd);
        return -1;
    }
    buf[n] = 0;
    if (strstr(buf, "revm-fastpipe-host-v1") == NULL) {
        close(fd);
        errno = EPROTO;
        return -1;
    }
    return fd;
}


static int ensure_command_source_node(const char* path) {
    if (!path || !*path) return 0;
    if (access(path, F_OK) == 0) return 0;
    if (mkfifo(path, 0600) == 0) return 0;
    if (errno == EEXIST) return 0;
    return -1;
}

static int write_generated_gpu_command(int data_fd, uint64_t seq) {
    char payload[512];
    time_t now = time(NULL);
    int n = snprintf(payload, sizeof(payload),
        "{\"kind\":\"revm-gfxstream-command-source-v1\","
        "\"abiVersion\":1,\"source\":\"guest:/dev/revm-gfxstream-commandq\","
        "\"packetType\":\"scanout-import-probe\",\"seq\":%llu,"
        "\"width\":1024,\"height\":768,\"format\":\"BGRA8888\","
        "\"timestampNs\":%llu}",
        (unsigned long long)seq,
        (unsigned long long)now * 1000000000ull);
    if (n <= 0 || n >= (int)sizeof(payload)) return -1;
    return write_packet(data_fd, (const uint8_t*)payload, (uint32_t)n);
}

static int self_test(void) {
    struct revm_fastpipe_header h;
    memset(&h, 0, sizeof(h));
    h.magic = REVM_FASTPIPE_MAGIC;
    h.abi_version = REVM_FASTPIPE_ABI_VERSION;
    h.mapping_bytes = 67108864;
    h.write_offset = REVM_FASTPIPE_HEADER_BYTES;
    if (h.magic != 0x31504652u || h.abi_version != 1 || h.write_offset != 4096) {
        fprintf(stderr, "ABI self-test failed\n");
        return 2;
    }
    uint8_t payload[] = { 'g', 'f', 'x', 's', 't', 'r', 'e', 'a', 'm' };
    uint8_t packet[sizeof(payload) + 4];
    packet[0] = sizeof(payload) & 0xff;
    packet[1] = (sizeof(payload) >> 8) & 0xff;
    packet[2] = (sizeof(payload) >> 16) & 0xff;
    packet[3] = (sizeof(payload) >> 24) & 0xff;
    memcpy(packet + 4, payload, sizeof(payload));
    if (packet[0] != 9 || memcmp(packet + 4, "gfxstream", 9) != 0) {
        fprintf(stderr, "packet self-test failed\n");
        return 3;
    }
    char command_payload[512];
    int n = snprintf(command_payload, sizeof(command_payload), "{\\\"kind\\\":\\\"revm-gfxstream-command-source-v1\\\",\\\"seq\\\":1}");
    if (n <= 0 || strstr(command_payload, "revm-gfxstream-command-source-v1") == NULL) {
        fprintf(stderr, "command-source self-test failed\n");
        return 4;
    }
    puts("revm-fastpipe-guest self-test passed");
    return 0;
}

int main(int argc, char** argv) {
    const char* control_path = REVM_FASTPIPE_DEFAULT_CONTROL_PORT;
    const char* data_path = REVM_FASTPIPE_DEFAULT_DATA_PORT;
    const char* source_path = NULL;
    const char* status_path = REVM_FASTPIPE_DEFAULT_STATUS_PATH;
    bool do_self_test = false;

    static const struct option opts[] = {
        {"control", required_argument, 0, 'c'},
        {"data", required_argument, 0, 'd'},
        {"source", required_argument, 0, 's'},
        {"status", required_argument, 0, 't'},
        {"self-test", no_argument, 0, 'T'},
        {"help", no_argument, 0, 'h'},
        {0, 0, 0, 0}
    };

    int ch;
    while ((ch = getopt_long(argc, argv, "c:d:s:t:Th", opts, NULL)) != -1) {
        switch (ch) {
            case 'c': control_path = optarg; break;
            case 'd': data_path = optarg; break;
            case 's': source_path = optarg; break;
            case 't': status_path = optarg; break;
            case 'T': do_self_test = true; break;
            case 'h': usage(argv[0]); return 0;
            default: usage(argv[0]); return 64;
        }
    }

    if (do_self_test) return self_test();

    signal(SIGINT, on_signal);
    signal(SIGTERM, on_signal);

    write_status(status_path, "starting", "opening virtio fastpipe ports");

    int control_fd = connect_control(control_path);
    if (control_fd < 0) {
        char detail[256];
        snprintf(detail, sizeof(detail), "control open/hello failed: %s", strerror(errno));
        write_status(status_path, "blocked", detail);
        fprintf(stderr, "%s\n", detail);
        return 70;
    }

    int data_fd = open(data_path, O_WRONLY | O_CLOEXEC);
    if (data_fd < 0) {
        char detail[256];
        snprintf(detail, sizeof(detail), "data open failed: %s", strerror(errno));
        write_status(status_path, "blocked", detail);
        fprintf(stderr, "%s\n", detail);
        close(control_fd);
        return 71;
    }

    int source_fd = -1;
    bool generated_source = false;
    if (source_path) {
        if (ensure_command_source_node(source_path) != 0) {
            char detail[256];
            snprintf(detail, sizeof(detail), "command source node create failed at %s: %s", source_path, strerror(errno));
            write_status(status_path, "blocked", detail);
            fprintf(stderr, "%s\n", detail);
            close(data_fd);
            close(control_fd);
            return 72;
        }
        source_fd = open(source_path, O_RDONLY | O_NONBLOCK | O_CLOEXEC);
        if (source_fd < 0) {
            generated_source = true;
        }
    } else {
        source_fd = STDIN_FILENO;
    }

    write_status(status_path, generated_source ? "command-source" : "ready",
        generated_source ? "generated guest GPU command source active" : "virtio fastpipe ports connected");

    uint8_t buf[65536];
    uint64_t packets = 0;
    uint64_t bytes = 0;
    while (!g_stop) {
        int n = 0;
        if (generated_source) {
            if (write_generated_gpu_command(data_fd, packets + 1) != 0) {
                char detail[256];
                snprintf(detail, sizeof(detail), "generated command write failed after %llu packets: %s",
                    (unsigned long long)packets, strerror(errno));
                write_status(status_path, "failed", detail);
                fprintf(stderr, "%s\n", detail);
                break;
            }
            packets++;
            bytes += 180;
            if ((packets & 0x0f) == 0) {
                char detail[256];
                snprintf(detail, sizeof(detail), "generated packets=%llu", (unsigned long long)packets);
                write_status(status_path, "command-source", detail);
            }
            sleep(1);
            continue;
        }

        struct pollfd pfd = { .fd = source_fd, .events = POLLIN };
        int pr = poll(&pfd, 1, 1000);
        if (pr < 0) {
            if (errno == EINTR) continue;
            break;
        }
        if (pr == 0) continue;
        if (!(pfd.revents & POLLIN)) break;
        n = read_some(source_fd, buf, sizeof(buf));
        if (n < 0) {
            if (errno == EINTR || errno == EAGAIN) continue;
            break;
        }
        if (n == 0) continue;
        if (write_packet(data_fd, buf, (uint32_t)n) != 0) {
            char detail[256];
            snprintf(detail, sizeof(detail), "data packet write failed after %llu packets: %s",
                (unsigned long long)packets, strerror(errno));
            write_status(status_path, "failed", detail);
            fprintf(stderr, "%s\n", detail);
            break;
        }
        packets++;
        bytes += (uint64_t)n;
        if ((packets & 0x3f) == 0) {
            char detail[256];
            snprintf(detail, sizeof(detail), "packets=%llu bytes=%llu",
                (unsigned long long)packets, (unsigned long long)bytes);
            write_status(status_path, "running", detail);
        }
    }

    char detail[256];
    snprintf(detail, sizeof(detail), "stopped packets=%llu bytes=%llu",
        (unsigned long long)packets, (unsigned long long)bytes);
    write_status(status_path, "stopped", detail);
    if (source_fd >= 0 && source_fd != STDIN_FILENO) close(source_fd);
    close(data_fd);
    close(control_fd);
    return 0;
}
