# Audio

Two projects split the audio system the same way graphics is split:

| Project | Owns | Dependencies |
|---|---|---|
| `MNVTEngine.Audio` | The software mixer `MixingAudioDevice` (voices, buses, insert effects, meters, panning and attenuation), `IAudioDevice`/`IAudioVoice`/`IAudioBuffer`, `IStreamingVoice` (PCM queued while it plays, e.g. [browser page audio](browser.md#audio)), the effects (`AudioEffects`), WAV, Ogg Vorbis, MP3, WebM Opus and MP4 AAC decoding, `AudioSource`/`AudioListener`/`AudioMixer`, per-scene `AudioSystem`, silent `NullAudioDevice` | Scenes, NVorbis (MIT), NLayer (MIT), Concentus (BSD-3), SharpJaad.AAC (MIT) |
| `MNVTEngine.Audio.OpenAL` | `OpenALAudioDevice.Create` (the mixer on an `OpenALAudioOutput`) | Audio, Silk.NET.OpenAL (MIT), OpenAL Soft native (`soft_oal.dll`, LGPL) |

The host chooses the backend:

- The game host, the Player, `mnvt run` and the editor's Play Mode pass `OpenALAudioDevice.Create`.
- Runtime and Scenes never reference OpenAL. This is enforced by the architecture tests.
- If no output device exists, the host logs a warning and the game runs silently on `NullAudioDevice`.
- Sound starts on the system's default output. `IAudioDevice.Outputs` lists the backend's playback devices (OpenAL
  Soft's `ALC_ENUMERATE_ALL_EXT`, `OpenALOutputSource`), and `UseOutput(name)` moves the mix to one of them while
  everything keeps playing: the old output's thread stops, and the time until the new one starts is mixed silently.
  A device that cannot be opened falls back to the default, and the call throws to say why. `null` means the
  default; the silent device has no outputs (`NotSupportedException`).

**The engine mixes the sound itself.** `MixingAudioDevice` mixes every voice into its mixer bus, runs each bus's
insert effects, fader and meter, feeds the result to its parent bus and finally to Master. OpenAL only plays the
result: one source refilled with 48 kHz stereo blocks (4 x 512 frames, about 43 ms queued) by an "Audio output"
thread. The game's calls and that thread share one lock. `NullAudioDevice` is the same mixer without an output, so
tests and headless runs mix, meter and time sound exactly as it would be heard.

**OpenAL Soft licence.** OpenAL Soft is LGPL. It is loaded dynamically from `Dependencies/Native/soft_oal.dll`, so players can replace it. Include its licence notice when you distribute a game.

## Components

**`AudioSource`:**

- **Settings:** `Clip` (a `.wav` or `.ogg` asset), `Volume`, `Pitch` (0.1–4), `Loop`, `PlayOnStart`, `Spatial`, `ReferenceDistance` (full volume up to here), `MaxDistance` (attenuation stops here).
- **Methods:** `Play()`, `Pause()`, `Resume()`, `Stop()`, and `PlayOneShot(clip, volumeScale)`.
- **State:** `IsPlaying` and `IsPaused`.
- **Position:** `Time` is the seconds into the playing clip (0 when stopped); setting it seeks, clamped to the clip. A streamed clip restarts decoding at the new frame, so the jump is heard after one decoded block.
- **One-shots** overlap freely and are recycled when they finish.
- **Disabling** the component stops it; destroying it releases its voice.

**`AudioListener`** marks where the player hears from. Without one, the main camera is used.

```csharp
public sealed class Jumper : Component
{
    [AssetType(AssetTypes.Audio)] public AssetRef JumpSound;

    public override void Update()
    {
        if (Input.GetKeyDown(Key.Space))
        {
            GameObject.GetComponent<AudioSource>()!.PlayOneShot(JumpSound);
        }
    }
}
```

In `.mnvts`: `audio.play("ObjectName")` and `audio.stop("ObjectName")` control the `AudioSource` on a named object.

## Mixer

Add one `AudioMixer` component to a scene (or to a prefab used in every scene).

- **Groups:**
  - **Master** always exists; its settings are `MasterVolume` and `MasterMuted`.
  - Every other group has a volume, a mute flag, a parent group and insert effects. The default parent is Master; missing or cyclic parents count as Master.
  - The inspector edits names and parents (add, remove), and shows the mixing console below them; edits are undoable.
- **Routing:** `AudioSource.Group` names the group a source plays through; empty or unknown means Master. Each group is
  a bus: its voices are summed, run through its effects (in order, before the fader), its fader and mute, then fed to
  its parent, up to Master (whose effects, fader and mute come last). The result is what `EffectiveVolume` predicts
  for the level, now with effects in between.
- **Changes apply in the same frame,** also to sounds that are already playing; fader moves are ramped over a block
  (about 5 ms), so they do not click.
- **From code:** `mixer.SetVolume("Music", 0.5f)`, `mixer.SetMuted("Effects", true)`, and `mixer.EffectiveVolume("Footsteps")`. From `.mnvts`: `audio.set_volume("Music", 0.5)` and `audio.mute("Effects", true)`.

A settings menu typically calls `SetVolume` on Music and Effects from sliders. The Platformer sample routes its jump and coin sounds through `Effects`.

### Effects

Each group (and Master: `AudioMixer.MasterEffects`) has a list of insert effects, `AudioEffectSettings`: a type id,
on/off, and parameter values (missing values take their defaults, values are clamped to their ranges). Effects keep
their state while parameters change, so reverb and delay tails continue while you adjust them.

| Effect (id) | Parameters |
|---|---|
| Low Pass (`lowpass`), High Pass (`highpass`) | Cutoff 20 Hz to 20 kHz, Resonance (Q) |
| EQ Three (`eq3`) | Low, Mid, High gain (-24 to +12 dB), Low Freq, High Freq (shelf corners; Mid is centred between them) |
| Compressor (`compressor`) | Threshold, Ratio, Attack, Release, Makeup (peak detection, stereo-linked) |
| Delay (`delay`) | Time up to 2 s, Feedback, Dry/Wet |
| Reverb (`reverb`) | Size, Damping, Width, Dry/Wet (Freeverb design) |
| Saturator (`distortion`) | Drive, Output, Dry/Wet (tanh soft clipping) |
| Chorus (`chorus`) | Rate, Depth, Dry/Wet (two modulated taps, opposite phase left and right) |

From code: `mixer.Find("Music")!.Effects.Add(AudioEffects.Find(AudioEffects.Reverb)!.CreateSettings());`, then change
`Parameters["mix"]`; the mixer picks the change up on the next frame. An effect type the engine does not know is
skipped (the rest of the chain plays).

### Meters and the mixing console

Every bus is metered after its fader: peak and RMS per channel over the last ~50 ms, the highest peak since the last
reset, and whether it clipped (went over 0 dBFS). `IAudioDevice.TryGetMeter("Music", out var meter)`,
`ResetMeter("Music")`.

Every bus also keeps its newest 4096 samples (`MixingAudioDevice.RecentFrames`, after the fader, mixed to mono) for
frequency analysis: `IAudioDevice.ReadBus("Music", samples)` copies them out, and `AudioSpectrum` (a Hann-windowed
`Fft`, 2048 samples by default: 43 ms, 23 Hz per bin at 48 kHz) turns them into per-bin amplitudes, with
`Band(lowHz, highHz)` summing a range. Read once a frame, the bass band's rises find kick drums (Borealise's dancers hop
on them). `Fft` is a plain radix-2 complex FFT for any power-of-two size.

The console (`AudioMixerView`, in the style of a DAW's mixer) shows one strip per group and Master: name and parent,
insert effects (checkbox to bypass, click to edit parameters, move up, remove, "+ FX" to add), the channel on/mute
button, the fader in dB (double-click for 0 dB), the peak-hold readout (red after clipping, click to reset) and a
stereo meter: RMS solid, peak lighter and falling 26 dB/s, green up to -6 dB, yellow to 0 dB, red above, scale -60 to
+6 dB. It appears in the editor's **Mixer** window (bottom tabs), in the AudioMixer inspector, and in the in-game debug
UI's **Mixer** panel (changes there last for the run). Meters are live in Play mode and in running games.

## Spatial audio

- **Positioning.** Spatial sources follow their GameObject, and the listener follows the `AudioListener` (or the camera). Both are updated every frame in the XY plane.
- **Panning.** Spatial mono voices are panned by their direction from the listener (equal power: a sound to the right
  plays in the right channel), in the mixer.
- **Attenuation.** Inverse distance, clamped: full volume up to `ReferenceDistance`, `reference / distance` beyond,
  no further attenuation past `MaxDistance` (as OpenAL's inverse-distance-clamped model).
- **Stereo clips** are not spatialised: they play as recorded. Non-spatial mono clips play centred (-3 dB per channel).

## Resource lifetime

- **Clips.** Each clip is loaded once per scene on first use, then cached: decoded to 16-bit PCM in memory, or streamed (below). A missing or undecodable clip logs one error and never plays; it never throws into game code.
- **Streaming.** Long clips (music, ambience, dialogue) are decoded while they play: each playing voice opens its own decoder over the asset's stream (`IAssetReader.OpenRead`, through the virtual file system), and a streaming thread keeps about 0.75 s decoded ahead in a ring buffer, outside the mixer's lock, so the audio thread never waits for reading or decoding. The first block is decoded when `Play` is called, so playback starts without a gap. The import setting `"loadType"` decides: `"auto"` (default: streamed when the decoded clip would exceed `AudioSystem.StreamingThreshold`, 4 MiB, about 24 s of 44.1 kHz stereo), `"decompressOnLoad"` or `"streaming"`. Looping streams rewind the decoder, so the loop point is seamless. A read or decoding error stops that voice and is logged (`IAudioDevice.TakeStreamError`); if decoding ever falls behind, the voice plays silence until it catches up and the `audio.underruns` metric counts it. Metrics: `audio.streams`, `audio.underruns`.
- **Unload.** `Scene.Unloaded` releases every voice and buffer of the scene.
- **The device** belongs to the host and is disposed at exit. If voices or buffers are still alive then, a warning is logged.
- **Packaged games.** Exported games read clips from the content packages through the virtual file system; streamed clips read only the chunks they reach. Build validation requires `soft_oal.dll` in `Dependencies/Native`.

## Decoding

- **WAV:** 8, 16, 24 and 32-bit PCM, 32-bit IEEE float, and `WAVE_FORMAT_EXTENSIBLE`.
- **Ogg Vorbis:** through NVorbis.
- **MP3:** through NLayer (MIT, fully managed): MPEG-1/2 layers I-III, recognised by an ID3v2 tag or a frame sync.
  The length is unknown until decoded (variable bit rates), so a streamed MP3 reports no frame count.
- **Opus in WebM:** through Concentus (BSD-3, fully managed), the first Opus track, always 48 kHz: what YouTube
  serves as audio. The file is read front to back, its blocks indexed a cluster at a time as playback reaches them;
  a seek indexes on to the target and starts decoding 80 ms before it so the decoder settles. The pre-skip is not
  heard. Laced blocks are refused.
- **AAC in fragmented MP4** (what HLS and DASH serve, SoundCloud among them): split by the engine and decoded with
  SharpJaad.AAC (MIT, fully managed; AAC-LC and HE-AAC), on any platform, front to back a fragment at a time. A seek
  walks the fragments to the target and decodes one frame ahead. It does not give its length. The edit list is
  ignored, so the encoder's priming (about 50 ms) is heard as silence.
- **AAC in MP4 without fragments:** Windows only, through Media Foundation's source reader (`mfreadwrite.dll`, part
  of Windows), at the file's own rate. It opens the file by path, so only a `FileStream` works; from memory or a
  package such an MP4 is refused.
- **Files still being written** (a download): WebM Opus and fragmented MP4 need no length and read in order, so a
  stream whose reads wait for the next bytes (and return 0 once the download ends) plays while it arrives; a read
  cut off mid-frame is the end of the audio. The mixer's streamed buffers read on the streaming thread and decode
  the first block on the caller's, so for a download decode on your own thread into an `IStreamingVoice`.
- **Channels:** mono and stereo only.
- **Errors:** damaged or unsupported files raise `InvalidDataException` with the asset name.
- **Streams:** `AudioDecoder.Open(stream, name)` gives an `IAudioSampleSource` (block reads, `Seek(frame)`); `AudioDecoder.Decode` drains one.
  A streamed clip of unknown length (`Duration` 0: MP3, fragmented MP4) still seeks with `IAudioVoice.Time`; past its end it finishes.

## Tests

- `NullAudioDevice` renders the mix in simulated time, so component behaviour (pause, finish, loops, one-shots, unload), bus levels, meters, panning and effects are tested deterministically (`MixerAndEffectTests`).
- `OpenAL_PlaysAClipToTheEnd` plays a quiet beep on the real device, and skips on machines without audio output.
- `OutputSwitchTests`: switching outputs stops the old one and starts the new one; a device that cannot be opened falls back to the default.
- `WebmOpusTests`: a WebM built in the test decodes to its length (without the pre-skip) and seeks.
- `StreamedDecodingTests`: a fragmented MP4 tone (`tests/TestAssets/Audio`, AAC from Windows' encoder) decodes and
  seeks to the frame; WebM and MP4 decode from a stream that gives 64 bytes per read and no length, as a download does.
- `AudioStreamingTests`: streamed decoding equals whole decoding, playback to the end without underruns, seamless looping, seeking (`Time`) in streamed and decoded clips, read errors stopping the voice, and the `loadType` / size rule.

## Limitations

- Effects are inserts on groups: no send/return buses, no sidechain (ducking), no Doppler effect, and no 3D listener orientation. The mixer is per scene, not a shared asset.
- Output is 48 kHz stereo; surround layouts are not supported.
- `PlayOneShot` voices cannot be stopped individually.
