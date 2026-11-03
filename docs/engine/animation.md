# Sprite Animation

Frame-by-frame animation from sprite sheets.

## Clips (`.mnvtanim`)

```json
{
  "schemaVersion": 1,
  "texture": "asset:0f5e0000-0000-4000-8000-000000000040",
  "frameWidth": 16,
  "frameHeight": 16,
  "columns": 4,
  "frames": [0, 1, 2, 1],
  "frameRate": 8,
  "loop": true
}
```

- **Sheet layout.** The sheet is a grid of `frameWidth × frameHeight` cells, counted from the image's top-left. Sheet index `n` is at column `n % columns`, row `n / columns`.
- **Frames.** `frames` lists sheet indices in playback order, and may repeat them (ping-pong, holds).
- **Rate and looping.** `frameRate` is in frames per second, from 0 to 240. `loop` defaults to true.
- **Validation.** The asset importer and the runtime use the same parser (`SpriteAnimationClip`, in Serialization). An invalid clip fails import, and the editor and `mnvt build` show every problem with its JSON path.
- **Dependencies.** The sheet texture is recorded as a dependency.
- **Sharp pixel art.** Give the sheet the `point` filter.

## Components

- **`SpriteRenderer.SourceRect`** selects part of the texture in texels from the top-left. Zero size means the whole texture.
- **`SpriteAnimator`** drives the `SpriteRenderer` on its own GameObject by setting its `Texture` and `SourceRect`.
  - Settings: `Clip`, `Speed`, `PlayOnStart`.
  - Runtime state: `IsPlaying`, `CurrentClip`, `Frame`, `PlaybackTime`, and the `Finished` event, raised once when a non-looping clip ends.

```csharp
public sealed class HeroAnimation : Component
{
    [AssetType(AssetTypes.Animation)] public AssetRef Idle, Run, Jump;

    public override void Update()
    {
        var animator = GameObject.GetComponent<SpriteAnimator>()!;
        var body = GameObject.GetComponent<Rigidbody2D>()!;
        // Saying which clip should show every frame is fine: Play ignores the clip that is already playing.
        animator.Play(!grounded ? Jump : MathF.Abs(body.Velocity.X) > 0.1f ? Run : Idle);
    }
}
```

`Play(clip, restart: true)` restarts a clip. `Pause`, `Resume` and `Stop` (which shows the first frame) control playback.

Clips are read through the scene's text asset reader, so exported games load them from the encrypted content archive. Each animator parses a clip only once. A missing or invalid clip logs one error and leaves the sprite unchanged.

## Limitations

- There is no animation state machine or blending; game code chooses the clip.
- Per-frame events and variable frame durations are not supported. Repeat an index in `frames` to hold a frame longer.
- Clips cannot animate properties other than the sprite (position, color, ...).
