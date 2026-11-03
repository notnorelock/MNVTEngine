# Audio

Two projects split the audio system the same way graphics is split:

| Project | Owns | Dependencies |
|---|---|---|
| `MNVTEngine.Audio` | `IAudioDevice`/`IAudioVoice`/`IAudioBuffer`, WAV and Ogg Vorbis decoding, `AudioSource`/`AudioListener`, per-scene `AudioSystem`, silent `NullAudioDevice` | Scenes, NVorbis (MIT) |
| `MNVTEngine.Audio.OpenAL` | `OpenALAudioDevice` | Audio, Silk.NET.OpenAL (MIT), OpenAL Soft native (`soft_oal.dll`, LGPL) |

The host chooses the backend:

- The game host, the Player, `mnvt run` and the editor's Play Mode pass `OpenALAudioDevice.Create`.
- Runtime and Scenes never reference OpenAL. This is enforced by the architecture tests.
- If no output device exists, the host logs a warning and the game runs silently on `NullAudioDevice`.

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
  - Every other group has a volume, a mute flag and a parent group. The default is Master; missing or cyclic parents count as Master.
  - The inspector edits groups directly: name, parent, volume slider, mute, add and remove. Edits are undoable.
- **Routing:** `AudioSource.Group` names the group a source plays through; empty means Master. Its effective volume is the source volume times each group's volume up the chain to Master, and 0 if any of them is muted.
- **Changes apply on the next frame,** also to sounds that are already playing, one-shots included.
- **From code:** `mixer.SetVolume("Music", 0.5f)`, `mixer.SetMuted("Effects", true)`, and `mixer.EffectiveVolume("Footsteps")`. From `.mnvts`: `audio.set_volume("Music", 0.5)` and `audio.mute("Effects", true)`.

A settings menu typically calls `SetVolume` on Music and Effects from sliders. The Platformer sample routes its jump and coin sounds through `Effects`.

## Spatial audio

- **Positioning.** Spatial sources follow their GameObject, and the listener follows the `AudioListener` (or the camera). Both are updated every frame in the XY plane.
- **Attenuation.** OpenAL's inverse-distance-clamped model applies.
- **Stereo clips.** OpenAL spatialises **mono** clips only; stereo clips play unattenuated.
- **Non-spatial sources** are relative to the listener, so they always play at full volume.

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

- `NullAudioDevice` simulates playback time, so component behaviour (pause, finish, loops, one-shots, unload) is tested deterministically.
- `OpenAL_PlaysAClipToTheEnd` plays a quiet beep on the real device, and skips on machines without audio output.

## Limitations

- Clips are fully decoded into memory; there is no streaming for long music.
- There are no effects (reverb, filters, ducking), no Doppler effect, and no 3D listener orientation. The mixer is per scene, not a shared asset.
- `PlayOneShot` voices cannot be stopped individually.
