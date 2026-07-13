#!/system/bin/sh
# REPlayer guest fastpipe bootstrap. This intentionally uses Android's built-in
# shell so the ramdisk can open the virtio ports before a native bionic bridge is
# available. It does not use ADB/display/QMP.
CONTROL=${CONTROL:-/dev/virtio-ports/revm.fastpipe.control}
DATA=${DATA:-/dev/virtio-ports/revm.fastpipe.data}
SOURCE=${SOURCE:-/dev/revm-gfxstream-commandq}
STATUS=${STATUS:-/data/local/tmp/revm-fastpipe-guest.status}

write_status() {
    mkdir -p /data/local/tmp 2>/dev/null
    echo "{\"kind\":\"revm-fastpipe-guest-sh-v1\",\"state\":\"$1\",\"detail\":\"$2\"}" > "$STATUS" 2>/dev/null
}

ensure_virtio_port_nodes() {
    mkdir -p /dev/virtio-ports 2>/dev/null
    for p in /sys/class/virtio-ports/*; do
        [ -f "$p/name" ] || continue
        name=`cat "$p/name" 2>/dev/null`
        case "$name" in
            revm.fastpipe.control|revm.fastpipe.data)
                if [ ! -e "/dev/virtio-ports/$name" ] && [ -f "$p/dev" ]; then
                    majmin=`cat "$p/dev" 2>/dev/null`
                    major=${majmin%:*}
                    minor=${majmin#*:}
                    mknod "/dev/virtio-ports/$name" c "$major" "$minor" 2>/dev/null
                    chmod 600 "/dev/virtio-ports/$name" 2>/dev/null
                fi
                ;;
        esac
    done
}

write_status starting "waiting for virtio fastpipe ports"
while true; do
    ensure_virtio_port_nodes
    if [ -e "$CONTROL" ] && [ -e "$DATA" ]; then
        break
    fi
    write_status waiting-ports "$CONTROL $DATA"
    sleep 1
done

# Open control bidirectionally, send the host hello, and read one response line.
while true; do
    exec 3<>"$CONTROL" 2>/dev/null && break
    write_status waiting-control "$CONTROL"
    sleep 1
done
printf 'hello\n' >&3
read HOST_REPLY <&3
write_status control-ready "$HOST_REPLY"

send_diag() {
    printf 'diag:%s\n' "$1" >&3
    read _ACK <&3
}

send_diag_cmd() {
    label="$1"
    shift
    out=`"$@" 2>&1 | tr '\r\n' '  ' | cut -c 1-700`
    send_diag "$label=$out"
}

run_android_sh() {
    if [ -x /system/bin/sh ]; then
        /system/bin/sh -c "$1" 2>&1
    elif [ -x /android/system/bin/sh ]; then
        chroot /android /system/bin/sh -c "$1" 2>&1
    else
        sh -c "$1" 2>&1
    fi
}

bootstrap_adb_network() {
    send_diag "bootstrap=begin"
    for cmd in \
        'mkdir -p /data/misc/adb' \
        'if [ -f /revm-adbkey.pub ]; then cp /revm-adbkey.pub /data/misc/adb/adb_keys; chown system:shell /data/misc/adb /data/misc/adb/adb_keys; chmod 02750 /data/misc/adb; chmod 0640 /data/misc/adb/adb_keys; fi' \
        'ip link set eth0 up' \
        'ip link set wifi_eth up' \
        'ip addr add 10.0.2.15/24 dev eth0' \
        'ip addr add 10.0.2.15/24 dev wifi_eth' \
        'ip rule add from all lookup main pref 1' \
        'ip route add default via 10.0.2.2 dev eth0' \
        'ip route add default via 10.0.2.2 dev wifi_eth' \
        'setprop service.adb.tcp.port 5555' \
        'setprop persist.adb.tcp.port 5555' \
        'setprop persist.service.adb.tcp.port 5555' \
        'setprop service.adb.root 1' \
        'stop adbd' \
        'start adbd'
    do
        out=`run_android_sh "$cmd" | tr '\r\n' '  ' | cut -c 1-220`
        send_diag "bootstrap_cmd=$cmd :: $out"
    done
    send_diag "bootstrap=end"
}

send_android_diag() {
    send_diag "probe=begin pid=$$ oneshot=${ONESHOT:-0} shell=$0 control=$CONTROL data=$DATA"
    bootstrap_adb_network
    send_framework_diag
    send_diag "probe=end"
}

send_framework_diag() {
    send_diag_cmd proc_cmdline cat /proc/cmdline
    send_diag_cmd virtio_names sh -c 'for p in /sys/class/virtio-ports/*/name; do [ -f "$p" ] && echo "$p=$(cat "$p")"; done'
    if [ -x /system/bin/getprop ]; then
        send_diag_cmd getprop_adbd /system/bin/getprop init.svc.adbd
        send_diag_cmd getprop_zygote /system/bin/getprop init.svc.zygote
        send_diag_cmd getprop_surfaceflinger /system/bin/getprop init.svc.surfaceflinger
        send_diag_cmd getprop_boot_completed /system/bin/getprop sys.boot_completed
        send_diag_cmd getprop_hw sh -c '/system/bin/getprop ro.hardware; /system/bin/getprop ro.boot.hardware; /system/bin/getprop ro.hardware.gralloc; /system/bin/getprop ro.hardware.hwcomposer'
        send_diag_cmd getprop_abi sh -c '/system/bin/getprop ro.product.cpu.abilist; /system/bin/getprop ro.product.cpu.abilist64; /system/bin/getprop ro.product.cpu.abilist32; /system/bin/getprop ro.zygote'
        send_diag_cmd ps_framework sh -c '/system/bin/ps -A 2>/dev/null | grep -E "system_server|zygote|surfaceflinger|bootanim|hwservicemanager|servicemanager" || true'
        send_diag_cmd logcat_framework sh -c '/system/bin/logcat -b all -d -t 120 2>/dev/null | grep -Ei "SurfaceFlinger|zygote|system_server|EGL|GLES|gralloc|hwcomposer|boot_progress|FATAL|crash" | tail -20 || true'
        send_diag_cmd logcat_system_server sh -c '/system/bin/logcat -b all -d -t 1000 2>/dev/null | grep -Ei "SystemServer|system_server|AndroidRuntime|RuntimeInit|ActivityManager|PackageManager|ServiceManager|Watchdog|RescueParty|lowmemorykiller|lmkd|Killing|am_proc_died|FATAL EXCEPTION|Fatal signal|Service .*zygote|received signal" | tail -80 || true'
        send_diag_cmd logcat_crash sh -c '/system/bin/logcat -b all -d -t 600 2>/dev/null | grep -Ei "signal 11|SIGSEGV|zygote_secondary|NativeCrashListener|RescueParty|tombstone|Fatal signal|DEBUG|Service .*zygote|received signal|system_server|AndroidRuntime|FATAL EXCEPTION" | tail -50 || true'
        send_diag_cmd dmesg_framework sh -c 'dmesg 2>/dev/null | grep -Ei "lowmemory|oom|killed|zygote|system_server|surfaceflinger|virtio|I/O error|blocked" | tail -60 || true'
        send_diag_cmd tombstones sh -c 'ls -lt /data/tombstones 2>/dev/null | head -12 || true'
        send_diag_cmd newest_tombstone_head sh -c 't=$(ls -t /data/tombstones/tombstone_* 2>/dev/null | head -1); [ -n "$t" ] && { echo "$t"; head -80 "$t"; } || true'
        send_diag_cmd newest_tombstone_bt sh -c 't=$(ls -t /data/tombstones/tombstone_* 2>/dev/null | head -1); [ -n "$t" ] && grep -Ei -A24 "pid:|signal|backtrace|fault addr|Abort message" "$t" | head -120 || true'
        send_diag_cmd ip_addr /system/bin/ip addr
        send_diag_cmd ip_rule /system/bin/ip rule
        send_diag_cmd ip_route /system/bin/ip route
        send_diag_cmd ps_adbd sh -c '/system/bin/ps -A 2>/dev/null | grep adbd || true'
    elif [ -x /android/system/bin/sh ]; then
        send_diag_cmd getprop_adbd chroot /android /system/bin/sh -c 'getprop init.svc.adbd'
        send_diag_cmd getprop_zygote chroot /android /system/bin/sh -c 'getprop init.svc.zygote'
        send_diag_cmd getprop_surfaceflinger chroot /android /system/bin/sh -c 'getprop init.svc.surfaceflinger'
        send_diag_cmd getprop_boot_completed chroot /android /system/bin/sh -c 'getprop sys.boot_completed'
        send_diag_cmd getprop_hw chroot /android /system/bin/sh -c 'getprop ro.hardware; getprop ro.boot.hardware; getprop ro.hardware.gralloc; getprop ro.hardware.hwcomposer'
        send_diag_cmd ps_framework chroot /android /system/bin/sh -c 'ps -A 2>/dev/null | grep -E "system_server|zygote|surfaceflinger|bootanim|hwservicemanager|servicemanager" || true'
        send_diag_cmd logcat_framework chroot /android /system/bin/sh -c 'logcat -b all -d -t 120 2>/dev/null | grep -Ei "SurfaceFlinger|zygote|system_server|EGL|GLES|gralloc|hwcomposer|boot_progress|FATAL|crash" | tail -20 || true'
        send_diag_cmd ip_addr chroot /android /system/bin/sh -c 'ip addr'
        send_diag_cmd ip_rule chroot /android /system/bin/sh -c 'ip rule'
        send_diag_cmd ip_route chroot /android /system/bin/sh -c 'ip route'
        send_diag_cmd ps_adbd chroot /android /system/bin/sh -c 'ps -A 2>/dev/null | grep adbd || true'
    else
        send_diag "android_tools=missing"
    fi
}

send_android_diag

while true; do
    exec 4>"$DATA" 2>/dev/null && break
    write_status waiting-data "$DATA"
    sleep 1
done
write_status ready "fastpipe ports open; starting guest GPU command source"
if [ "$ONESHOT" = "1" ]; then
    exit 0
fi

# Concrete command source for this Android-x86 image.  The host data queue
# requires 4-byte little-endian packet lengths.  Prefer Android's own raw
# screencap stream: uint32 width, uint32 height, uint32 pixel_format, then pixels.
# This is real guest scanout data over fastpipe, not QMP/scrcpy/ADB display.
[ -e "$SOURCE" ] || mknod "$SOURCE" p 2>/dev/null || mkfifo "$SOURCE" 2>/dev/null || true
chmod 600 "$SOURCE" 2>/dev/null || true
FRAME=/data/local/tmp/revm-fastpipe-scanout.raw
GPU_PACKET='{"kind":"revm-gfxstream-command-source-v1","abiVersion":1,"source":"guest:/dev/revm-gfxstream-commandq","packetType":"scanout-import-probe"}'

write_le32() {
    n=$1
    b0=$((n & 255))
    b1=$(((n >> 8) & 255))
    b2=$(((n >> 16) & 255))
    b3=$(((n >> 24) & 255))
    esc=`printf '\\%03o\\%03o\\%03o\\%03o' "$b0" "$b1" "$b2" "$b3"`
    printf "$esc" >&4
}

write_packet_file() {
    file=$1
    bytes=`wc -c < "$file" 2>/dev/null | tr -d ' '`
    case "$bytes" in ''|*[!0-9]*) return 1;; esac
    [ "$bytes" -gt 16 ] || return 1
    write_le32 "$bytes"
    cat "$file" >&4
    return 0
}

write_probe_packet() {
    # length=141 => little-endian 0x8d 00 00 00
    printf '\215\000\000\000%s\n' "$GPU_PACKET" >&4
}

write_status command-source "$SOURCE real_scanout=screencap_raw"
send_diag "gpu_command_source=ready source=$SOURCE mode=screencap_raw fallback=probe"
while true; do
    if [ -x /system/bin/screencap ]; then
        /system/bin/screencap > "$FRAME" 2>/dev/null && write_packet_file "$FRAME" && { sleep 1; continue; }
    fi
    if [ -x /android/system/bin/screencap ]; then
        chroot /android /system/bin/screencap > "$FRAME" 2>/dev/null && write_packet_file "$FRAME" && { sleep 1; continue; }
    fi
    send_diag "gpu_scanout=screencap_unavailable fallback=probe"
    write_probe_packet
    sleep 1
done
