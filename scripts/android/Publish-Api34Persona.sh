#!/usr/bin/env bash
set -euo pipefail

ROOT="${1:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)}"
SDK="$ROOT/runtime/google-emulator/sdk"
AVD_HOME="$ROOT/runtime/google-emulator/avd-home"
SOURCE_NAME="${REPLAYER_SOURCE_AVD:-Api34Official}"
PUBLISH_MODE="${REPLAYER_PERSONA_MODE:-stealth}"
case "$PUBLISH_MODE" in
  stealth)
    DEFAULT_STAGE_NAME="PersonaPublish"
    DEFAULT_TARGET_NAME="ReVM"
    DEFAULT_REPORT_NAME="persona-publication.json"
    ;;
  compatibility)
    DEFAULT_STAGE_NAME="PersonaPublishResizable"
    DEFAULT_TARGET_NAME="ReVMResizable"
    DEFAULT_REPORT_NAME="persona-publication-resizable.json"
    ;;
  *)
    printf 'ERROR: REPLAYER_PERSONA_MODE must be stealth or compatibility\n' >&2
    exit 1
    ;;
esac
STAGE_NAME="${REPLAYER_STAGE_AVD:-$DEFAULT_STAGE_NAME}"
TARGET_NAME="${REPLAYER_TARGET_AVD:-$DEFAULT_TARGET_NAME}"
SERIAL="${REPLAYER_PUBLISH_SERIAL:-emulator-5584}"
PORT="${SERIAL#emulator-}"
ADB="$SDK/platform-tools/adb.exe"
EMULATOR="$SDK/emulator/emulator.exe"
APKSIGNER="$SDK/build-tools/33.0.2/apksigner.bat"
PERSONA="${REPLAYER_PERSONA_FILE:-$ROOT/android-tools/replayer-persona/personas/replayer-api34.json}"
PATCHER="$ROOT/scripts/android/persona_properties.py"
SOURCE_AVD="$AVD_HOME/$SOURCE_NAME.avd"
STAGE_AVD="$AVD_HOME/$STAGE_NAME.avd"
TARGET_AVD="$AVD_HOME/$TARGET_NAME.avd"
WORK="$ROOT/runtime/google-emulator/build/persona-publish-$PUBLISH_MODE"
REPORT="$ROOT/runtime/google-emulator/$DEFAULT_REPORT_NAME"
LOG_DIR="$WORK/logs"
CUSTOMIZER="$ROOT/ReVM/Assets/Android/replayer-customizer.apk"
DISABLED_PACKAGES="$ROOT/android-tools/replayer-customizer/baseline-disabled-packages.txt"
EMULATOR_PID=""

fail() { printf 'ERROR: %s\n' "$*" >&2; exit 1; }
winpath() { wslpath -w "$1"; }
adb() { "$ADB" -s "$SERIAL" "$@"; }
stop_emulator() {
  adb emu kill >/dev/null 2>&1 || true
  if [[ -n "${EMULATOR_PID:-}" ]]; then
    for _ in $(seq 1 30); do
      kill -0 "$EMULATOR_PID" 2>/dev/null || break
      sleep 1
    done
    wait "$EMULATOR_PID" 2>/dev/null || true
  fi
  EMULATOR_PID=""
}
remove_stage_transients() {
  local attempt
  for attempt in $(seq 1 30); do
    if rm -rf "$STAGE_AVD/snapshots" "$STAGE_AVD"/*.lock "$STAGE_AVD/launch.lock" \
        "$STAGE_AVD/multiinstance.lock" "$STAGE_AVD/tmpAdbCmds" 2>/dev/null; then
      return 0
    fi
    sleep 1
  done
  fail "Staging AVD locks remained held after emulator shutdown"
}
signer_digest() {
  local apk="$1" output digest
  output="$(/mnt/c/Windows/System32/cmd.exe /d /c "$(winpath "$APKSIGNER") verify --print-certs $(winpath "$apk")" | tr -d '\r')"
  digest="$(python3 -c "import sys; lines=[x for x in sys.stdin.read().splitlines() if 'certificate SHA-256 digest:' in x]; print(lines[0].split(':',1)[1].strip() if lines else '')" <<< "$output")"
  [[ "$digest" =~ ^[0-9a-f]{64}$ ]] || fail "Could not read signer digest for $apk"
  printf '%s' "$digest"
}
contains_line() {
  local needle="$1" haystack="$2" line
  while IFS= read -r line; do
    [[ "$line" == "$needle" ]] && return 0
  done <<< "$haystack"
  return 1
}
verify_disabled_packages() {
  local installed disabled package_name required=0 missing=""
  installed="$(adb shell pm list packages | tr -d '\r')"
  disabled="$(adb shell pm list packages -d | tr -d '\r')"
  while IFS= read -r package_name; do
    package_name="${package_name%%#*}"
    package_name="$(printf '%s' "$package_name" | xargs)"
    [[ -z "$package_name" ]] && continue
    if contains_line "package:$package_name" "$installed"; then
      required=$((required + 1))
      contains_line "package:$package_name" "$disabled" || missing="${missing}${missing:+, }$package_name"
    fi
  done < "$DISABLED_PACKAGES"
  [[ -z "$missing" ]] || fail "Package policy still enables: $missing"
  printf '%s' "$required"
}
apply_dark_mode() {
  adb shell cmd uimode night yes >/dev/null
  adb shell settings put secure ui_night_mode 2
}
verify_dark_mode() {
  local label="$1" service secure state
  service="$(adb shell cmd uimode night | tr -d '\r')"
  secure="$(adb shell settings get secure ui_night_mode | tr -d '\r')"
  state="$(adb shell dumpsys uimode | tr -d '\r' | grep -E 'mComputedNightMode|mCurUiMode' | tr '\n' ' ')"
  [[ "$service" == 'Night mode: yes' ]] || fail "$label Android night service is not enabled: $service"
  [[ "$secure" == '2' ]] || fail "$label secure ui_night_mode is not 2: $secure"
  [[ "$state" == *'mComputedNightMode=true'* && "$state" == *'mCurUiMode=0x21'* ]] || fail "$label night configuration is not active: $state"
  printf '%s\n%s\n%s' "$service" "$secure" "$state"
}
wait_boot() {
  local deadline=$((SECONDS + 300))
  while (( SECONDS < deadline )); do
    if [[ "$(adb get-state 2>/dev/null | tr -d '\r')" == device ]] &&
       [[ "$(adb shell getprop sys.boot_completed 2>/dev/null | tr -d '\r')" == 1 ]]; then
      return 0
    fi
    sleep 2
  done
  fail "Timed out waiting for $SERIAL to boot"
}
wait_disconnect() {
  local deadline=$((SECONDS + 60))
  while (( SECONDS < deadline )); do
    [[ "$(adb get-state 2>/dev/null | tr -d '\r')" != device ]] && return 0
    sleep 1
  done
}
cleanup_process() {
  if [[ -n "$EMULATOR_PID" ]]; then
    adb emu kill >/dev/null 2>&1 || true
    sleep 3
    kill "$EMULATOR_PID" >/dev/null 2>&1 || true
  fi
}
trap cleanup_process EXIT

[[ -f "$EMULATOR" ]] || fail "Android Emulator is missing: $EMULATOR"
[[ -f "$ADB" ]] || fail "ADB is missing: $ADB"
[[ -f "$APKSIGNER" ]] || fail "apksigner is missing: $APKSIGNER"
[[ -d "$SOURCE_AVD" ]] || fail "Source AVD is missing: $SOURCE_AVD"
[[ -f "$PERSONA" ]] || fail "Persona is missing: $PERSONA"
[[ -f "$PATCHER" ]] || fail "Property patcher is missing: $PATCHER"
[[ -f "$AVD_HOME/$SOURCE_NAME.ini" ]] || fail "Source AVD descriptor is missing"
mapfile -t PERSONA_EXPECTED < <(python3 - "$PERSONA" <<'PY'
import json,sys
persona=json.load(open(sys.argv[1],encoding='utf-8'))
print(persona['model'])
print(persona['device'])
PY
)
EXPECTED_MODEL="${PERSONA_EXPECTED[0]}"
EXPECTED_DEVICE="${PERSONA_EXPECTED[1]}"

if "$ADB" devices | tr -d '\r' | grep -q "^$SERIAL[[:space:]]"; then
  fail "$SERIAL is already in use"
fi

rm -rf "$WORK" "$STAGE_AVD"
rm -f "$AVD_HOME/$STAGE_NAME.ini"
mkdir -p "$WORK/input" "$WORK/output" "$LOG_DIR"
cp -a "$SOURCE_AVD" "$STAGE_AVD"
rm -rf "$STAGE_AVD/snapshots" "$STAGE_AVD"/*.lock "$STAGE_AVD"/multiinstance.lock \
  "$STAGE_AVD"/tmpAdbCmds "$STAGE_AVD"/system.img.qcow2 "$STAGE_AVD"/vendor.img.qcow2 \
  "$STAGE_AVD"/product.img.qcow2 "$STAGE_AVD"/system_ext.img.qcow2 "$STAGE_AVD"/odm.img.qcow2

python3 - "$STAGE_AVD/config.ini" "$STAGE_NAME" <<'PY'
from pathlib import Path
import sys
path, name = Path(sys.argv[1]), sys.argv[2]
lines = path.read_text(errors='replace').splitlines()
updates = {
    'AvdId': name, 'avd.ini.displayname': name,
    'fastboot.forceColdBoot': 'yes', 'fastboot.forceFastBoot': 'no',
    'snapshot.present': 'no', 'PlayStore.enabled': 'false',
    'hw.gpu.enabled': 'yes', 'hw.gpu.mode': 'host',
    'hw.resizable.configs': 'phone-0-1080-2400-420, foldable-1-1768-2208-420, tablet-2-1920-1200-240',
}
out=[]; seen=set()
for line in lines:
    key=line.split('=',1)[0] if '=' in line else ''
    if key in updates:
        out.append(f'{key}={updates[key]}'); seen.add(key)
    elif not key.endswith('.lock'):
        out.append(line)
for key,value in updates.items():
    if key not in seen: out.append(f'{key}={value}')
path.write_text('\n'.join(out)+'\n')
PY
printf 'avd.ini.encoding=UTF-8\r\npath=%s\r\npath.rel=avd/%s.avd\r\ntarget=android-34\r\n' \
  "$(winpath "$STAGE_AVD")" "$STAGE_NAME" > "$AVD_HOME/$STAGE_NAME.ini"

if [[ "${REPLAYER_SKIP_APK_BUILD:-0}" != 1 ]]; then
  "/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe" -NoProfile -ExecutionPolicy Bypass \
    -File "$(winpath "$ROOT/scripts/android/Build-Customizer.ps1")" >/dev/null
  "/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe" -NoProfile -ExecutionPolicy Bypass \
    -File "$(winpath "$ROOT/scripts/android/Build-SettingsOverlay.ps1")" >/dev/null
fi
[[ -f "$CUSTOMIZER" ]] || fail "Customizer build did not produce an APK"
OVERLAY="$ROOT/runtime/google-emulator/build/settings-overlay/REPlayerSettingsIdentityOverlay.apk"
[[ -f "$OVERLAY" ]] || fail "Settings overlay build did not produce an APK"
CUSTOMIZER_SIGNER_SHA256="$(signer_digest "$CUSTOMIZER")"
OVERLAY_SIGNER_SHA256="$(signer_digest "$OVERLAY")"
[[ "$CUSTOMIZER_SIGNER_SHA256" == "$OVERLAY_SIGNER_SHA256" ]] || fail "Customizer and Settings overlay signer certificates differ"

export ANDROID_AVD_HOME="$AVD_HOME"
export ANDROID_SDK_ROOT="$SDK"
export WSLENV="ANDROID_AVD_HOME/wp:ANDROID_SDK_ROOT/wp"
"$EMULATOR" -avd "$STAGE_NAME" -ports "$PORT,$((PORT+1))" -writable-system -wipe-data \
  -no-window -no-boot-anim -no-snapshot -gpu host -accel on -no-audio -no-sim \
  -camera-back none -camera-front none -no-metrics \
  >"$LOG_DIR/emulator.stdout.log" 2>"$LOG_DIR/emulator.stderr.log" &
EMULATOR_PID=$!
wait_boot

adb root >/dev/null
sleep 2
adb wait-for-device >/dev/null
adb disable-verity | tee "$WORK/disable-verity.txt"
adb reboot >/dev/null
wait_disconnect || true
wait_boot
adb root >/dev/null
sleep 2
adb wait-for-device >/dev/null
adb remount | tee "$WORK/remount.txt"

declare -A REMOTE=(
  [system]='/system/build.prop'
  [product]='/product/etc/build.prop'
  [system_ext]='/system_ext/etc/build.prop'
  [vendor]='/vendor/build.prop'
  [odm]='/odm/etc/build.prop'
)
for name in system product system_ext vendor odm; do
  adb pull "${REMOTE[$name]}" "$(winpath "$WORK/input/$name.build.prop")" >/dev/null
done
python3 "$PATCHER" --persona "$PERSONA" --input-dir "$WORK/input" --output-dir "$WORK/output" --mode "$PUBLISH_MODE"
for name in system product system_ext vendor odm; do
  adb push "$(winpath "$WORK/output/$name.build.prop")" "${REMOTE[$name]}" >/dev/null
  adb shell chmod 0644 "${REMOTE[$name]}"
  adb shell restorecon "${REMOTE[$name]}" >/dev/null 2>&1 || true
done
adb shell mkdir -p /product/overlay
adb push "$(winpath "$OVERLAY")" /product/overlay/REPlayerSettingsIdentityOverlay.apk >/dev/null
adb shell chmod 0644 /product/overlay/REPlayerSettingsIdentityOverlay.apk
adb shell restorecon /product/overlay/REPlayerSettingsIdentityOverlay.apk >/dev/null 2>&1 || true
adb install -r "$(winpath "$CUSTOMIZER")" >/dev/null
adb shell am start -W -n com.replayer.utility/.ProvisionActivity >/dev/null
adb shell "settings put secure theme_customization_overlay_packages '{\"android.theme.customization.color_source\":\"preset\",\"android.theme.customization.theme_style\":\"MONOCHROMATIC\"}'"
adb shell settings put secure themed_icon 1
adb shell settings put secure themed_icons 1
apply_dark_mode
adb shell cmd package set-home-activity com.google.android.apps.nexuslauncher/.NexusLauncherActivity >/dev/null
while IFS= read -r package_name; do
  package_name="${package_name%%#*}"
  package_name="$(printf '%s' "$package_name" | xargs)"
  [[ -z "$package_name" ]] && continue
  adb shell pm disable-user --user 0 "$package_name" </dev/null >/dev/null 2>&1 || true
done < "$DISABLED_PACKAGES"
adb shell cmd package wait-for-handler --timeout 60000 >/dev/null 2>&1 || true
sleep 15
adb shell sync
sleep 2
PRE_REBOOT_DISABLED_COUNT="$(verify_disabled_packages)"
adb reboot >/dev/null
wait_disconnect || true
wait_boot

PROPS="$(adb shell 'getprop ro.product.model; getprop ro.product.device; getprop ro.build.fingerprint; getprop ro.build.type; getprop ro.build.tags; getprop ro.debuggable; getprop ro.adb.secure; getprop ro.product.cpu.abilist' | tr -d '\r')"
mapfile -t PROP_LINES <<< "$PROPS"
EXPECTED_DEBUGGABLE=0
if [[ "$PUBLISH_MODE" == compatibility ]]; then EXPECTED_DEBUGGABLE=1; fi
[[ "${PROP_LINES[0]:-}" == "$EXPECTED_MODEL" ]] || fail "Published model is not $EXPECTED_MODEL: ${PROP_LINES[0]:-missing}"
[[ "${PROP_LINES[1]:-}" == "$EXPECTED_DEVICE" ]] || fail "Published device is not $EXPECTED_DEVICE: ${PROP_LINES[1]:-missing}"
[[ "${PROP_LINES[3]:-}" == 'user' ]] || fail "Published build type is not user"
[[ "${PROP_LINES[4]:-}" == 'release-keys' ]] || fail "Published build tags are not release-keys"
[[ "${PROP_LINES[5]:-}" == "$EXPECTED_DEBUGGABLE" ]] || fail "ro.debuggable is not $EXPECTED_DEBUGGABLE for $PUBLISH_MODE"
[[ "${PROP_LINES[6]:-}" == '1' ]] || fail "ro.adb.secure is not 1"
[[ "${PROP_LINES[7]:-}" == x86_64,* ]] || fail "x86_64 is not the primary ABI"

ROOT_RESULT="$(adb root 2>&1 | tr -d '\r')"
if [[ "$PUBLISH_MODE" == stealth ]]; then
  [[ "$ROOT_RESULT" == *'cannot run as root'* ]] || fail "Release image unexpectedly allowed adb root: $ROOT_RESULT"
  [[ "$(adb shell id -u | tr -d '\r')" != '0' ]] || fail "Release image returned a root ADB shell"
else
  adb wait-for-device >/dev/null
  ROOT_UID="$(adb shell id -u | tr -d '\r')"
  [[ "$ROOT_UID" == '0' ]] || fail "Resizable image did not provide the documented root ADB shell: output='$ROOT_RESULT', uid='$ROOT_UID'"
  ROOT_RESULT="${ROOT_RESULT:-adb root completed}; uid=$ROOT_UID"
fi
OVERLAY_STATE="$(adb shell 'cmd overlay list | grep -A2 -B2 com.replayer.settings.overlay; cmd overlay lookup com.android.settings com.android.settings:string/about_settings' 2>&1 | tr -d '\r')"
[[ "$OVERLAY_STATE" == *'[x] com.replayer.settings.overlay'* ]] || fail "Settings overlay is not enabled: $OVERLAY_STATE"
[[ "$OVERLAY_STATE" == *'About phone'* ]] || fail "Settings overlay lookup did not resolve About phone"
CUSTOMIZER_STATE="$(adb shell pm path com.replayer.utility | tr -d '\r')"
[[ "$CUSTOMIZER_STATE" == package:* ]] || fail "Customizer package is missing"
HOME_STATE="$(adb shell cmd package resolve-activity --brief -a android.intent.action.MAIN -c android.intent.category.HOME | tr -d '\r')"
[[ "$HOME_STATE" == *'com.google.android.apps.nexuslauncher/.NexusLauncherActivity'* ]] || fail "Nexus Launcher is not HOME: $HOME_STATE"
THEME_STATE="$(adb shell settings get secure theme_customization_overlay_packages | tr -d '\r')"
[[ "$THEME_STATE" == *'MONOCHROMATIC'* ]] || fail "Monochromatic theme state is missing: $THEME_STATE"
[[ "$(adb shell settings get secure themed_icon | tr -d '\r')" == '1' ]] || fail "themed_icon is not enabled"
[[ "$(adb shell settings get secure themed_icons | tr -d '\r')" == '1' ]] || fail "themed_icons is not enabled"
WARM_DARK_STATE="$(verify_dark_mode Warm)"
WARM_DISABLED_COUNT="$(verify_disabled_packages)"

WARM_SAMPLE_START="$(date -u +'%Y-%m-%dT%H:%M:%SZ')"
WARM_PID_FIRST="$(adb shell pidof system_server | tr -d '\r')"
WARM_NETWORK_FIRST="$(adb shell pidof com.android.networkstack.process | tr -d '\r')"
sleep 60
WARM_PID_LAST="$(adb shell pidof system_server | tr -d '\r')"
WARM_NETWORK_LAST="$(adb shell pidof com.android.networkstack.process | tr -d '\r')"
WARM_SAMPLE_END="$(date -u +'%Y-%m-%dT%H:%M:%SZ')"
[[ -n "$WARM_PID_FIRST" && "$WARM_PID_FIRST" == "$WARM_PID_LAST" ]] || fail "system_server was not stable: $WARM_PID_FIRST -> $WARM_PID_LAST"
[[ -n "$WARM_NETWORK_FIRST" && "$WARM_NETWORK_FIRST" == "$WARM_NETWORK_LAST" ]] || fail "NetworkStack was not stable: $WARM_NETWORK_FIRST -> $WARM_NETWORK_LAST"

# A warm reboot is not sufficient evidence for overlayfs publication. Terminate
# QEMU completely and require the same state from a new writable-system process.
stop_emulator
"$EMULATOR" -avd "$STAGE_NAME" -ports "$PORT,$((PORT+1))" -writable-system \
  -no-window -no-boot-anim -no-snapshot -gpu host -accel on -no-audio -no-sim \
  -camera-back none -camera-front none -no-metrics \
  >"$LOG_DIR/cold-emulator.stdout.log" 2>"$LOG_DIR/cold-emulator.stderr.log" &
EMULATOR_PID=$!
wait_boot
COLD_PROPS="$(adb shell 'getprop ro.product.model; getprop ro.product.device; getprop ro.build.fingerprint; getprop ro.build.type; getprop ro.build.tags; getprop ro.debuggable; getprop ro.adb.secure; getprop ro.product.cpu.abilist' | tr -d '\r')"
[[ "$COLD_PROPS" == "$PROPS" ]] || fail "Cold-process properties differ from the published state"
PROPS="$COLD_PROPS"
ROOT_RESULT="$(adb root 2>&1 | tr -d '\r')"
if [[ "$PUBLISH_MODE" == stealth ]]; then
  [[ "$ROOT_RESULT" == *'cannot run as root'* ]] || fail "Cold-process release image unexpectedly allowed adb root: $ROOT_RESULT"
  [[ "$(adb shell id -u | tr -d '\r')" != '0' ]] || fail "Cold-process release image returned a root ADB shell"
else
  adb wait-for-device >/dev/null
  ROOT_UID="$(adb shell id -u | tr -d '\r')"
  [[ "$ROOT_UID" == '0' ]] || fail "Cold-process resizable image did not provide root ADB: output='$ROOT_RESULT', uid='$ROOT_UID'"
  ROOT_RESULT="${ROOT_RESULT:-adb root completed}; uid=$ROOT_UID"
fi
OVERLAY_STATE="$(adb shell 'cmd overlay list | grep -A2 -B2 com.replayer.settings.overlay; cmd overlay lookup com.android.settings com.android.settings:string/about_settings' 2>&1 | tr -d '\r')"
[[ "$OVERLAY_STATE" == *'[x] com.replayer.settings.overlay'* && "$OVERLAY_STATE" == *'About phone'* ]] || fail "Cold-process Settings overlay failed"
[[ "$(adb shell pm path com.replayer.utility | tr -d '\r')" == package:* ]] || fail "Cold-process customizer package is missing"
[[ "$(adb shell settings get secure theme_customization_overlay_packages | tr -d '\r')" == *'MONOCHROMATIC'* ]] || fail "Cold-process monochromatic theme is missing"
COLD_DARK_STATE="$(verify_dark_mode Cold-process)"
COLD_DISABLED_COUNT="$(verify_disabled_packages)"
COLD_SAMPLE_START="$(date -u +'%Y-%m-%dT%H:%M:%SZ')"
COLD_PID_FIRST="$(adb shell pidof system_server | tr -d '\r')"
COLD_NETWORK_FIRST="$(adb shell pidof com.android.networkstack.process | tr -d '\r')"
sleep 30
COLD_PID_LAST="$(adb shell pidof system_server | tr -d '\r')"
COLD_NETWORK_LAST="$(adb shell pidof com.android.networkstack.process | tr -d '\r')"
COLD_SAMPLE_END="$(date -u +'%Y-%m-%dT%H:%M:%SZ')"
[[ "$COLD_PID_FIRST" == "$COLD_PID_LAST" ]] || fail "Cold-process system_server was not stable: $COLD_PID_FIRST -> $COLD_PID_LAST"
[[ "$COLD_NETWORK_FIRST" == "$COLD_NETWORK_LAST" ]] || fail "Cold-process NetworkStack was not stable: $COLD_NETWORK_FIRST -> $COLD_NETWORK_LAST"

stop_emulator
remove_stage_transients
rm -rf "$STAGE_AVD/data/misc/pstore"
rm -f "$STAGE_AVD"/emulator-user.ini "$STAGE_AVD"/emu-launch-params.txt "$STAGE_AVD"/read-snapshot.txt \
  "$STAGE_AVD"/bootcompleted.ini "$STAGE_AVD"/quickbootChoice.ini "$STAGE_AVD"/hardware-qemu.ini \
  "$STAGE_AVD"/cache.img "$STAGE_AVD"/cache.img.qcow2
rm -rf "$TARGET_AVD"
rm -f "$AVD_HOME/$TARGET_NAME.ini"
mv "$STAGE_AVD" "$TARGET_AVD"
rm -f "$AVD_HOME/$STAGE_NAME.ini"
python3 - "$TARGET_AVD" "$TARGET_NAME" "$SOURCE_NAME" "$PERSONA" "$REPORT" "$PROPS" "$ROOT_RESULT" "$OVERLAY_STATE" "$COLD_PID_FIRST" "$PUBLISH_MODE" "$ROOT" "$CUSTOMIZER_SIGNER_SHA256" "$OVERLAY_SIGNER_SHA256" "$WARM_SAMPLE_START" "$WARM_PID_FIRST" "$WARM_NETWORK_FIRST" "$WARM_SAMPLE_END" "$WARM_PID_LAST" "$WARM_NETWORK_LAST" "$COLD_SAMPLE_START" "$COLD_PID_FIRST" "$COLD_NETWORK_FIRST" "$COLD_SAMPLE_END" "$COLD_PID_LAST" "$COLD_NETWORK_LAST" "$WARM_DISABLED_COUNT" "$COLD_DISABLED_COUNT" "$PRE_REBOOT_DISABLED_COUNT" "$WARM_DARK_STATE" "$COLD_DARK_STATE" <<'PY'
from pathlib import Path
import sys,json,hashlib,datetime,subprocess
avd=Path(sys.argv[1]); target,source=sys.argv[2:4]; persona_path=Path(sys.argv[4]); report_path=Path(sys.argv[5])
props=sys.argv[6].splitlines(); root_result=sys.argv[7]; overlay_state=sys.argv[8]; pid=sys.argv[9]; mode=sys.argv[10]
root=Path(sys.argv[11]); customizer_signer=sys.argv[12]; overlay_signer=sys.argv[13]
warm=sys.argv[14:20]; cold=sys.argv[20:26]; package_counts=sys.argv[26:29]
dark=[state.splitlines() for state in sys.argv[29:31]]
config=avd/'config.ini'; lines=config.read_text(errors='replace').splitlines(); out=[]
for line in lines:
 key=line.split('=',1)[0] if '=' in line else ''
 if key=='AvdId': out.append(f'AvdId={target}')
 elif key=='avd.ini.displayname': out.append(f'avd.ini.displayname={target}')
 else: out.append(line)
config.write_text('\n'.join(out)+'\n')
def hash_file(p):
 h=hashlib.sha256()
 with p.open('rb') as f:
  while chunk:=f.read(8*1024*1024): h.update(chunk)
 return {'sha256':h.hexdigest(),'bytes':p.stat().st_size}
hashes={}
for p in sorted(avd.iterdir()):
 if p.is_file() and (p.suffix in ('.qcow2','.img') or p.name in ('config.ini','userdata-qemu.img')):
  hashes[p.name]=hash_file(p)
persona=json.loads(persona_path.read_text())
input_paths={
 'personaJson':persona_path,
 'propertyTransformer':root/'scripts/android/persona_properties.py',
 'publisher':root/'scripts/android/Publish-Api34Persona.sh',
 'settingsOverlayBuilder':root/'scripts/android/Build-SettingsOverlay.ps1',
 'customizerBuilder':root/'scripts/android/Build-Customizer.ps1',
 'customizerApk':root/'ReVM/Assets/Android/replayer-customizer.apk',
 'settingsOverlayApk':root/'runtime/google-emulator/build/settings-overlay/REPlayerSettingsIdentityOverlay.apk',
 'disabledPackages':root/'android-tools/replayer-customizer/baseline-disabled-packages.txt',
 'sourceAvdConfig':root/f'runtime/google-emulator/avd-home/{source}.avd/config.ini',
 'emulatorMetadata':root/'runtime/google-emulator/sdk/emulator/source.properties',
 'systemImageMetadata':root/'runtime/google-emulator/sdk/system-images/android-34/google_apis/x86_64/source.properties',
 'systemImage':root/'runtime/google-emulator/sdk/system-images/android-34/google_apis/x86_64/system.img',
 'vendorImage':root/'runtime/google-emulator/sdk/system-images/android-34/google_apis/x86_64/vendor.img',
 'userdataImage':root/'runtime/google-emulator/sdk/system-images/android-34/google_apis/x86_64/userdata.img',
 'gfxstreamManifest':root/'runtime/google-emulator/persona-emulator-37.1.7/replayer-gfxstream-persona.json'
}
missing=[name for name,p in input_paths.items() if not p.is_file()]
if missing: raise SystemExit('Missing provenance inputs: '+', '.join(missing))
inputs={name:hash_file(p) for name,p in input_paths.items()}
commit=subprocess.run(['git','rev-parse','HEAD'],cwd=root,text=True,capture_output=True).stdout.strip()
dirty=subprocess.run(['git','status','--porcelain','--untracked-files=no'],cwd=root,text=True,capture_output=True).stdout
report={'schema':1,'persona':persona['id'],'mode':mode,'sourceAvd':source,'targetAvd':target,
 'publishedAtUtc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'properties':props,
 'adbRoot':root_result,'overlay':overlay_state,'stableSystemServerPid':pid,
 'securityLane':'release-observation' if mode=='stealth' else 'resizable-analysis',
 'displayProfiles':{'0':{'width':1080,'height':2400,'density':420},'2':{'width':1920,'height':1200,'density':240}},
 'continuity':{
  'warm':{'startedAtUtc':warm[0],'systemServerFirst':warm[1],'networkStackFirst':warm[2],'endedAtUtc':warm[3],'systemServerLast':warm[4],'networkStackLast':warm[5],'minimumSeconds':60},
  'cold':{'startedAtUtc':cold[0],'systemServerFirst':cold[1],'networkStackFirst':cold[2],'endedAtUtc':cold[3],'systemServerLast':cold[4],'networkStackLast':cold[5],'minimumSeconds':30}},
 'packagePolicy':{'preRebootDisabledInstalledPackages':int(package_counts[2]),'warmDisabledInstalledPackages':int(package_counts[0]),'coldDisabledInstalledPackages':int(package_counts[1])},
 'darkMode':{'requested':'yes','secureUiNightMode':2,'warm':dark[0],'cold':dark[1]},
 'provenance':{'sourceCommit':commit or None,'sourceTreeDirty':bool(dirty.strip()),'inputs':inputs,
  'apkSignerSha256':{'customizer':customizer_signer,'settingsOverlay':overlay_signer}},
 'files':hashes}
(avd/'replayer-baseline.json').write_text(json.dumps(report,indent=2)+'\n')
report_path.write_text(json.dumps(report,indent=2)+'\n')
print(json.dumps({'target':str(avd),'files':len(hashes),'model':props[0],'stablePid':pid}))
PY
printf 'avd.ini.encoding=UTF-8\r\npath=%s\r\npath.rel=avd/%s.avd\r\ntarget=android-34\r\n' \
  "$(winpath "$TARGET_AVD")" "$TARGET_NAME" > "$AVD_HOME/$TARGET_NAME.ini"

printf 'Published %s from %s with report %s\n' "$TARGET_NAME" "$SOURCE_NAME" "$REPORT"
