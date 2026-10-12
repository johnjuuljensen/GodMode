#!/bin/bash
# usage: relloop.sh <launch|settled> <N> <tag>   (Release app: pm clear, UI taps)
export MSYS_NO_PATHCONV=1
A="/c/Program Files (x86)/Android/android-sdk/platform-tools/adb.exe"
MODE=$1; N=$2; TAG=$3; OUT=C:/tmp/gm274drive/runs/$TAG; mkdir -p $OUT; PKG=com.godmode.maui
adb_() { "$A" -s emulator-5600 "$@"; }
has() { adb_ shell uiautomator dump /sdcard/ui.xml >/dev/null 2>&1; adb_ shell cat /sdcard/ui.xml | grep -q "text=\"$1\""; }
for i in $(seq 1 $N); do
  adb_ shell am force-stop $PKG; adb_ shell pm clear $PKG >/dev/null; adb_ shell pm grant $PKG android.permission.POST_NOTIFICATIONS
  adb_ shell cmd notification cancel_all $PKG >/dev/null 2>&1
  adb_ logcat -b all -c; t0=$(date +%s%3N)
  adb_ shell am start -n $PKG/crc64d809cabfef7648d3.MainActivity >/dev/null
  for k in $(seq 1 60); do pid=$(adb_ shell pidof $PKG | tr -d '\r'); [ -n "$pid" ] && break; sleep 0.2; done
  sleep 9
  [ "$MODE" = settled ] && sleep 15
  adb_ shell input tap 809 2274; sleep 0.8
  sleep 1
  adb_ shell input tap 539 566; sleep 1.5
  adb_ shell input tap 540 746; adb_ shell input keycombination 113 29; adb_ shell input keyevent 67; adb_ shell input text "http://10.0.2.2:31374"
  adb_ shell input tap 540 950; adb_ shell input text "T274"
  adb_ shell input tap 540 1153; adb_ shell input text "test274key-not-secret-0123456789"
  adb_ exec-out screencap -p > $OUT/before-add-$i.png; adb_ shell input tap 540 1321; sent=$(( $(date +%s%3N) - t0 ))
  sleep 15
  now=$(adb_ shell pidof $PKG | tr -d '\r')
  adb_ logcat -b all -d > $OUT/logcat-$i.txt
  notes=$(adb_ shell dumpsys notification | grep -c "NotificationRecord.*pkg=$PKG.*id=1 tag=")
  fetched=$(grep -c "loadServers: fetched 1 servers" $OUT/logcat-$i.txt)
  crash=$(grep -E "Fatal signal|SIGSEGV|exited due to signal|FATAL EXCEPTION|Abort message|mono-rt" $OUT/logcat-$i.txt | grep -v "signal 9" | grep -c .)
  echo "$TAG #$i mode=$MODE pid=$pid after=$now sent_ms=$sent fetched1=$fetched notifications=$notes crashlines=$crash"
done
