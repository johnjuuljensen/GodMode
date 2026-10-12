#!/bin/bash
# usage: probe.sh <kind> ; launches the app, triggers the crash kind, saves logcat
export MSYS_NO_PATHCONV=1
A="/c/Program Files (x86)/Android/android-sdk/platform-tools/adb.exe"; PKG=com.godmode.maui; K=$1; OUT=C:/tmp/gm274drive/runs/probe; mkdir -p $OUT
"$A" -s emulator-5600 shell am force-stop $PKG; sleep 1
"$A" -s emulator-5600 logcat -b all -c
"$A" -s emulator-5600 shell am start -n $PKG/crc64d809cabfef7648d3.MainActivity >/dev/null
for k in $(seq 1 100); do pid=$("$A" -s emulator-5600 shell pidof $PKG | tr -d '\r'); [ -n "$pid" ] && break; sleep 0.2; done
for k in $(seq 1 200); do "$A" -s emulator-5600 shell cat /proc/net/unix | grep -q "webview_devtools_remote_$pid" && break; sleep 0.2; done
"$A" -s emulator-5600 forward tcp:9274 localabstract:webview_devtools_remote_$pid >/dev/null
for k in $(seq 1 100); do r=$(node C:/tmp/gm274drive/cdp.mjs 9274 "typeof window.HybridWebView" 2>/dev/null); [ "$r" = '"object"' ] && break; sleep 0.2; done
node C:/tmp/gm274drive/cdp.mjs 9274 "window.HybridWebView.SendRawMessage(JSON.stringify({Type:'debug.crash',Id:'p',Payload:{Kind:'$K'}})); 'sent'"
sleep ${WAIT:-8}
"$A" -s emulator-5600 logcat -b all -d > $OUT/$K.txt
echo "== $K pid=$pid alive_after=$("$A" -s emulator-5600 shell pidof $PKG | tr -d '\r')"
grep -E " $pid " $OUT/$K.txt | grep -E "Fatal signal|DEBUG|mono-rt|monodroid|crash|Stack overflow|stack overflow|SIGSEGV|abort|Abort|FATAL|Unhandled|libc" | grep -v -E "monodroid-assembly|open_from_bundles" | head -12
grep -E "exited due to signal|has died" $OUT/$K.txt | grep -v "signal 9" | head -3
