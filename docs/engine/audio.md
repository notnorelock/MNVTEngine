# Audio

Two projects split the audio system the same way graphics is split:

| Project | Owns | Dependencies |
|---|---|---|
| `MNVTEngine.Audio` | The software mixer `MixingAudioDevice` (voices, buses, insert effects, meters, panning and attenuation), `IAudioDevice`/`IAudioVoice`/`IAudioBuffer`, `IStreamingVoice` (PCM queued while it plays, e.g. [browser page audio](browser.md#audio)), the effects (`AudioEffects`), WAV and Ogg Vorbis decoding, `AudioSource`/`AudioListener`/`AudioMixer`, per-scene `AudioSystem`, silent `NullAudioDevice` | Scenes, NVorbis (MIT) |
| `MNVTEngine.Audio.OpenAL` | `OpenALAudioDevice.Create` (the mixer on an `OpenALAudioOutput`) | Audio, Silk.NET.OpenAL (MIT), OpenAL Soft native (`soft_oal.dll`, LGPL) |

The host chooses the backend:

- The game host, the Player, `mnvt run` and the editor's Play Mode pass `OpenALAudioDevice.Create`.
- Runtime and Scenes never reference OpenAL. This is enforced by the architecture tests.
- If no output device exists, the host logs a warning and the game runs silently on `NullAudioDevice`.

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

- **Clips.** Each clip is decoded to 16-bit PCM and uploaded once per scene on first use, then cached. A missing or undecodable clip logs one error and never plays; it never throws into game code.
- **Unload.** `Scene.Unloaded` releases every voice and buffer of the scene.
- **The device** belongs to the host and is disposed at exit. If voices or buffers are still alive then, a warning is logged.
- **Packaged games.** Exported games read clips from the encrypted content archive. Build validation requires `soft_oal.dll` in `Dependencies/Native`.

## Decoding

- **WAV:** 8, 16, 24 and 32-bit PCM, 32-bit IEEE float, and `WAVE_FORMAT_EXTENSIBLE`.
- **Ogg Vorbis:** through NVorbis.
- **Channels:** mono and stereo only.
- **Errors:** damaged or unsupported files raise `InvalidDataException` with the asset name.

## Tests

- `NullAudioDevice` renders the mix in simulated time, so component behaviour (pause, finish, loops, one-shots, unload), bus levels, meters, panning and effects are tested deterministically (`MixerAndEffectTests`).
- `OpenAL_PlaysAClipToTheEnd` plays a quiet beep on the real device, and skips on machines without audio output.

## Limitations

- Clips are fully decoded into memory; there is no streaming for long music.
- Effects are inserts on groups: no send/return buses, no sidechain (ducking), no Doppler effect, and no 3D listener orientation. The mixer is per scene, not a shared asset.
- Output is 48 kHz stereo; surround layouts are not supported.
- `PlayOneShot` voices cannot be stopped individually.
