#pragma once

#include <stdint.h>

#define REVM_FASTPIPE_MAGIC 0x31504652u /* RFP1 */
#define REVM_FASTPIPE_ABI_VERSION 1
#define REVM_FASTPIPE_HEADER_BYTES 4096
#define REVM_FASTPIPE_DEFAULT_CONTROL_PORT "/dev/virtio-ports/revm.fastpipe.control"
#define REVM_FASTPIPE_DEFAULT_DATA_PORT "/dev/virtio-ports/revm.fastpipe.data"
#define REVM_FASTPIPE_DEFAULT_STATUS_PATH "/data/local/tmp/revm-fastpipe-guest.status"

struct revm_fastpipe_header {
    uint32_t magic;
    int32_t abi_version;
    int64_t mapping_bytes;
    int64_t control_messages;
    int64_t data_messages;
    int64_t bytes_received;
    int64_t doorbell;
    int64_t write_offset;
};
