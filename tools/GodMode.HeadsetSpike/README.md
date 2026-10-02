# GodMode.HeadsetSpike

Issue #382's spike: a small Windows-only test app for a Bluetooth headset (the Shokz OpenRun Pro 2), separate from the
MAUI app. It answers the questions that decide steps 3 and 4 of #382's flow: which button gestures reach Windows,
whether GodMode can pause and resume Spotify, how long the A2DP↔HFP switches take, whether a button in HFP reaches
`CallControl`, and whether the headset has LE Audio.

It is **manual**: no test can press a headset button. The one piece of pure logic, the gesture classifier
(`GestureClassifier.cs`), has unit tests in `tests/GodMode.HeadsetSpike.Tests`.

## Run it

```powershell
git submodule update --init --recursive   # once per checkout: it uses VoiceBot's Windows audio
dotnet run --project tools/GodMode.HeadsetSpike/GodMode.HeadsetSpike.csproj
```

Or build it and start `tools/GodMode.HeadsetSpike/bin/Debug/net10.0-windows10.0.19041.0/GodMode.HeadsetSpike.exe`.
No install, keys or admin rights are needed. Close the GodMode app's voice first, so the two do not hold the mic at
once.

The log goes to `%LOCALAPPDATA%\GodMode.HeadsetSpike\logs\headset-<date>-<time>.log`, one file per run (**Open log
folder** shows it). Attach it to #382.

## What it shows and logs

The top of the window is the state: the mic (open or closed, and its level), the headset's profile as far as Windows
tells (a guess, below), what plays (GSMTC's current session), the call state, the app's own media controls, the key it
swallows, the last gesture, and the time since the last reference. Under it is each headset endpoint, its mix format
and whether it carries sound.

Every log line has the wall clock, the time since start, and the time since the last **reference** (`mic open+1234ms`).
A reference is a mic open or close, or an announcement test. So a switch's timing reads straight off the lines after it.

| Tag | Source |
|---|---|
| `HOOK` | Media and volume keys from a low-level keyboard hook (`WH_KEYBOARD_LL`), down and up, `injected` when software sent them, `SWALLOWED` when the app ate them |
| `RAW` | HID consumer-control reports (raw input, page 0x0C), in the background too, with the device that sent them: Windows' AVRCP transport is such a device |
| `APPCMD` | `WM_APPCOMMAND`, only while the window has focus |
| `GESTUR` | The classifier's reading of `HOOK` or `RAW` presses: single, double, triple, long, with how long it took |
| `SMTC` | The app's own `SystemMediaTransportControls`: `ButtonPressed`, while it has claimed them |
| `GSMTC` | Media sessions (`GlobalSystemMediaTransportControlsSessionManager`): sessions, the current one, each one's playback state and track, and each pause or play the app sent and what it returned |
| `CALL` | `Windows.Media.Devices.CallControl`: what the app reported, and `AnswerRequested`, `HangUpRequested` and the rest |
| `AUDIO` | Every endpoint at start, defaults, endpoints added, removed or changing state, the headset's property changes and mix formats, and sound starting and stopping on each headset endpoint (its peak meter) |
| `MIC` | WaveIn (the same capture GodMode.Maui's voice uses: VoiceBot's `NativeAudioSource`, `MicCapture.WaveIn`, 16 kHz mono, 100 ms buffers), opened and closed on a worker thread so the hook and Mark keep running: the moment you asked (the reference), the moment the open or close returned, the first buffer, the first buffer with sound in it |
| `TONE` | The tone's start and end, its device and format |
| `LEAUD` | The LE Audio probe |
| `MARK`, `NOTE` | You: **Mark** (or Ctrl+Alt+M anywhere) when you hear something, a note when you want to say what |

**The profile** is a guess, because Windows has no API that names the Bluetooth profile in use. The OpenRun Pro 2 shows
as `Headphones (OpenRun Pro 2 by Shokz)` (A2DP, render) and `Headset (OpenRun Pro 2 by Shokz)` (HFP, capture, and a
render endpoint of that name where Windows splits them). The guess is HFP while the app has the headset's mic open or
sound plays on a `Headset`/`Hands-Free` endpoint, and A2DP otherwise. Your ears are the check: the switch to HFP is
audible as the music dropping to phone quality.

## Before the trial

1. Pair the headset and connect it. Start Spotify and play something on the headset.
2. Start the app. Check the top: `Playing: SpotifyAB.SpotifyMusic…: Playing`, and the headset's endpoints listed under
   it. If the list is empty, type part of the headset's name in **Headset: name has** and press **Rescan endpoints**.
3. Pick the headset's mic in **Mic** (the default communications mic may be the laptop's), and in **Tone** the speaker
   the music plays on (*Default speaker*, if the headset is the default).
4. Write a **note** before each step below (`Q1 single press`), so the log says what you did.

## Trial script

Do them in this order. For each one, record what the table at the end asks for.

### Q1. Which gestures reach Windows as media keys while on A2DP? Can one be swallowed, while Spotify keeps play/pause?

Mic closed (A2DP), Spotify playing, the app's window **not** focused (click another window).

1. Single press. Then wait 2 s. Double press. Wait. Triple press. Wait. Long press (hold 2 s), and again holding 4 s.
   Then any other button the headset has (volume +/−, and their long presses).
   - Record, per gesture: the `HOOK` lines (which key, down and up), the `RAW` lines (which device), the `GESTUR` line,
     and what Spotify did (paused, next track, nothing).
   - The headset may classify gestures itself: a double press may arrive as one `NextTrack`, not as two `PlayPause`.
     Then `GESTUR` says *single NextTrack*, and the answer is "double press = NextTrack".
2. Pick the key a double or triple press sends (say `PreviousTrack` or `NextTrack`) in **Keys: swallow**. Repeat that
   gesture, then a single press.
   - Record: does Spotify still skip on the swallowed gesture (it should not), and does a single press still play/pause
     Spotify (it should)? The `HOOK` line says `SWALLOWED`.
   - If `RAW` shows a press and `HOOK` shows nothing, the key does not go through the keyboard path, and the hook cannot
     swallow it: say so.
3. Set swallow back to *(none)*. Press **Claim SMTC (playing)**: the app's own media controls become a playing
   session. Repeat the single and double press.
   - Record: the `SMTC ButtonPressed` lines, and whether Spotify still reacted. Expected from Windows' design: buttons go
     to the current session, so while the app claims it, Spotify gets nothing. Press **Release SMTC** after.

### Q2. Can it pause and resume Spotify through GSMTC?

1. Spotify playing. **Pause current**, wait 2 s, **Play current**. Then **Pause all playing**, **Resume paused**.
   - Record: did Spotify pause and resume, the time from the `TryPauseAsync` line to Spotify's `Paused` line, and from
     `TryPlayAsync` to `Playing` (subtract the `t=` values).
2. **Announcement test**: it pauses what plays, plays three tones over the speaker, and resumes.
   - Record: whether the tones were clear on the headset (A2DP), and the gap between `Paused` and the first tone.

### Q3. How long do A2DP→HFP and HFP→A2DP take, are they audible, and when can the tone play?

Spotify playing, so you hear the switch.

1. **Open mic**. Press **Mark** (or Ctrl+Alt+M) the moment you hear the music drop to phone quality. Wait 5 s.
   **Close mic**, and **Mark** when the music sounds full again. Do it three times.
   - Record: the `MARK` time after `mic open` (heard switch), after `mic close` (heard switch back), the `MIC open returned`,
     `first buffer` and `first sound` times after the open (and `close returned` after the close), the `AUDIO` lines (endpoint state, format, sound starting or
     stopping) around each, and whether there was a gap, a click or a pause in the music.
2. Tone offsets. Set **tone offset ms** to 0 and press **Open mic + rising tone**; then **Close mic + falling tone**.
   Repeat with offsets 250, 500, 1000, 1500 and 2000 ms. Then tick **tone waits for first mic sound**, with offset 0,
   and again with 200: speak or hum right after pressing, since the mic's first sound is a peak above 200 of 32767
   (without it the tone plays after 10 s anyway).
   - Record, per offset: did you hear the whole tone, part of it (cut off), or nothing; and the `TONE start` time after
     `mic open`. The smallest offset where the whole tone plays is the answer.
3. Repeat 2 with the speaker in **Tone** set to *Default communications speaker*, if that is a different endpoint.

### Q4. While in HFP, does a button press reach `CallControl` once the app reports an active call? Does Windows show a call?

1. **Open mic** (HFP). Press **Report active call**. Look at the taskbar and the system tray (a call indicator, a
   microphone-in-use icon). Single press the headset's button, then long press it.
   - Record: any `CALL` line (`HangUpRequested`, `AnswerRequested`), any `HOOK`/`RAW` line, and what Windows showed.
2. **End call**, then **Report incoming (silent)**, and single press: an `AnswerRequested` is the headset's answer.
   Then **Report incoming (ringing)**: does the headset ring? **End call**, **Close mic**.
   - If the log says `no CallControl`, set the headset as Windows' default communications device (Sound settings) and
     press **Report active call** again: the app looks for it at each request.
3. Without opening the mic, press **Report active call**: does reporting a call by itself switch the headset to HFP?

### Q5. Does the headset support LE Audio (LC3)?

1. Press **Probe LE Audio**. Record the `LEAUD` lines.
2. Look in Windows' Settings › Bluetooth & devices › Devices for a *Use LE Audio when available* switch, and record
   whether it is there.

## What documentation and code already say

- **LE Audio (Q5): almost certainly no.** The OpenRun Pro 2's specification lists Bluetooth 5.3 with A2DP, AVRCP and
  HFP, and SBC as its only codec ([EE's spec page](https://ee.co.uk/help/tech/device/Shokz/OpenRunPro2), and retailers'
  copies of Shokz's spec sheet); no LE Audio, LC3 or TMAP. Windows has no public API that says which codec or transport
  an audio endpoint uses: Microsoft's own check is the *Use LE Audio* switch in Settings, there only when both the PC
  and the device support it ([Windows Insider blog on LE Audio super wideband stereo](https://techcommunity.microsoft.com/tag/le%20audio)).
  What an app can see is the paired device: an LE Audio headset is a paired Bluetooth LE device with the LE Audio GATT
  services (Published Audio Capabilities 0x1850, Audio Stream Control 0x184E, from the Bluetooth SIG's assigned
  numbers), which **Probe LE Audio** looks for. On this machine's first run the headset showed only as Classic endpoints
  (`Headphones (OpenRun Pro 2 by Shokz)` and `Headset (OpenRun Pro 2 by Shokz)`).
- **SMTC and the current session (Q1.3)**: Windows sends media buttons to one session, the current one, so an app's own
  SMTC hears them only while it is that session, and Spotify does not get them then. That is Windows' design, which the
  trial confirms or not.
- **GSMTC (Q2)**: `TryPauseAsync` and `TryPlayAsync` are requests the app may refuse, and return whether it took them;
  Spotify's desktop app is a GSMTC session (on this machine: `SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify`, read by
  the app's first run). Whether it obeys is the trial's.
- **CallControl (Q4)**: on this machine, with the headset off, `CallControl.GetDefault()` gave none: Windows offers call
  control only for an audio device whose driver takes call commands, which an HFP headset's may.
- **Switch timing (Q3)**: the app times from the mic's open (WaveIn start), as GodMode.Maui's voice would open it. The
  `first sound` line is when the mic's audio stops being silence: a Bluetooth link that is not up yet gives zeros first.
  Its resolution is the 100 ms buffer.

## Results (for the user to fill in)

Date, Windows build (the log's first line), headset firmware:

| # | Question | What to record | Result |
|---|---|---|---|
| 1a | Single press on A2DP | `HOOK` key, `RAW` device, Spotify's reaction | |
| 1b | Double press | as 1a | |
| 1c | Triple press | as 1a | |
| 1d | Long press (2 s, 4 s) | as 1a | |
| 1e | Other buttons (volume, their long presses) | as 1a | |
| 1f | Swallow one gesture's key, keep play/pause | swallowed gesture ignored by Spotify? single press still works? | |
| 1g | Own SMTC claimed | `SMTC ButtonPressed` lines? Spotify still reacts? | |
| 2a | GSMTC pause and resume Spotify | worked? latency to `Paused` / `Playing` (ms) | |
| 2b | Announcement test | tones clear over A2DP? gap from `Paused` to tone (ms) | |
| 3a | A2DP→HFP (mic open) | heard at (ms), first buffer (ms), first sound (ms), audible how (gap, click) | |
| 3b | HFP→A2DP (mic close) | heard at (ms), audible how, music back by itself? | |
| 3c | Rising tone after open | smallest offset (ms) with the whole tone; with "waits for first mic sound" | |
| 3d | Falling tone after close | smallest offset (ms) with the whole tone, and on which speaker | |
| 4a | Button in HFP with an active call | `HangUpRequested`/`AnswerRequested`? other lines? | |
| 4b | Windows' call indicator | shown? where? | |
| 4c | Reporting a call without the mic | did the headset switch to HFP? | |
| 5 | LE Audio | `LEAUD` result; *Use LE Audio* switch in Settings? | |

Attach the log files of the runs to #382.
