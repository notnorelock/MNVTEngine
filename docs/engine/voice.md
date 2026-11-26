# Microphones and voice

The `audio` module records from microphones and ships the pieces of a voice chat: Opus encoding and decoding, a
jitter buffer and voice activity detection. Sending packets is the game's (any transport: Galaxy's unreliable
messages suit it), and so is routing them on the server.

Code: `MNVTEngine.Audio` (`IAudioCaptureSource`, `IAudioCapture`), `MNVTEngine.Audio.OpenAL` (`OpenALCaptureSource`),
`MNVTEngine.Audio.Voice` (everything else). Tests: `Audio.Tests` (`CaptureTests`, skipped without a microphone),
`Audio.Voice.Tests`.

## Recording

`IAudioDevice.Capture` lists the microphones and opens one; it is null on the silent device and on a backend that
cannot record. OpenAL Soft's capture functions are called from `soft_oal` directly (the Silk.NET binding has none).

```csharp
if (AudioSystem.For(Scene)?.Device.Capture is { } microphones)
{
    using var mic = microphones.Open(device: null, VoiceFormat.SampleRate); // InvalidOperationException: none
    mic.Start();
    // each frame: var read = mic.Read(buffer); framer.Push(buffer.AsSpan(0, read));
}
```

Recording is 16-bit mono. OpenAL keeps about a second of samples until they are read; read every frame.

## The voice format and codec

`VoiceFormat`: 48 kHz mono, 20 ms frames of 960 samples, packets of at most 400 bytes.

- `VoiceFramer` cuts recorded blocks of any size into whole frames (keeping at most a second).
- `VoiceEncoder` (Concentus, managed Opus): speech mode, 24 kbps by default, in-band forward error correction tuned
  for 10 % loss. About 60 bytes a frame.
- `VoiceDecoder`: `Decode` a packet, `Recover` a lost frame from the next packet's error correction, `Conceal` a lost
  frame from what came before. A damaged packet is concealed, never thrown: packets come from the network.
- `VoiceActivity`: a frame louder than `ThresholdDb` (-42 dBFS) is speech; speech continues for `HangoverFrames` (300 ms)
  of quiet so word ends and short pauses are kept. `LevelDb` drives a meter. Push-to-talk needs none of it.

## Receiving

One `VoiceReceiver` per speaker: its `VoiceJitterBuffer` and decoder. `Add(sequence, packet)` as packets arrive (in any
order, sequence numbers wrapping at 65536); `Next(frame)` every 20 ms of playback gives PCM, or false for silence.

- Playback of a talk spurt starts once the target (2 frames, 40 ms) is waiting.
- A frame that did not arrive while later ones did is lost: rebuilt from the next packet's FEC, or concealed.
- Packets behind the playback point are dropped; one far off (50 frames) restarts the stream.
- Running dry stops playback and raises the target by a frame, up to 6 (120 ms); 5 s without running dry lowers it
  again, so the delay follows the network's jitter.

Feed the frames to an `IStreamingVoice` (`IAudioDevice.CreateStreamingVoice(1, VoiceFormat.SampleRate)`, bus `Voice`),
keeping a little queued (`QueuedSeconds` around 40-60 ms) so the output never starves between game frames.

## Limits

- The capture device is fixed when opened; a microphone unplugged while recording stops delivering samples.
- No echo cancellation or noise suppression: players use headphones, or the threshold of `VoiceActivity`.
- Streaming voices are not positioned; a game attenuates a speaker by distance with the voice's `Volume`.
