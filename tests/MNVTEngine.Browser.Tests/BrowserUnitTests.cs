using System.IO.Pipes;
using System.Runtime.InteropServices;
using MNVTEngine.BrowserHosting;
using MNVTEngine.Mathematics;
using MNVTEngine.Scenes;
using Newtonsoft.Json.Linq;

namespace MNVTEngine.Browser.Tests;

public sealed class SecurityPolicyTests
{
    private static BrowserSecurityPolicy Policy(BrowserSecurityProfile profile, BrowserCapabilities capabilities = BrowserCapabilities.None, params string[] domains) => new(profile, domains, capabilities);

    [Theory]
    [InlineData("mnvt://game/index.html", "Web/index.html")]
    [InlineData("mnvt://game/ui/menu.html?x=1#top", "Web/ui/menu.html")]
    [InlineData("mnvt://game/", "Web/index.html")]
    [InlineData("mnvt://GAME/a%20b.css", "Web/a b.css")]
    public void GamePaths_MapIntoAssetsWeb(string url, string expected)
    {
        Assert.True(BrowserSecurityPolicy.TryGetGameAssetPath(url, out var path));
        Assert.Equal(expected, path);
    }

    [Theory]
    [InlineData("mnvt://game/%2e%2e/Settings/content.key")]
    [InlineData("mnvt://game/a/%2E%2E/%2E%2E/x")]
    [InlineData("mnvt://game/..%5c..%5cx")]
    [InlineData("mnvt://game/a%5cb")]
    [InlineData("mnvt://game/C:/Windows/win.ini")]
    [InlineData("mnvt://game/a//b")]
    [InlineData("mnvt://game/a/%00b")]
    [InlineData("mnvt://other/index.html")]
    [InlineData("https://game/index.html")]
    [InlineData("not a url")]
    public void GamePaths_RejectEscapes(string url) => Assert.False(BrowserSecurityPolicy.TryGetGameAssetPath(url, out _));

    [Fact]
    public void GamePaths_NormalizedDotSegmentsStayInsideWeb()
    {
        // Uri resolves "a/../b" itself; whatever it yields must stay under Web/.
        if (BrowserSecurityPolicy.TryGetGameAssetPath("mnvt://game/a/../../../b.html", out var path))
        {
            Assert.Equal("Web/b.html", path);
        }
    }

    [Theory]
    [InlineData(BrowserSecurityProfile.TrustedLocal, "mnvt://game/index.html", true)]
    [InlineData(BrowserSecurityProfile.TrustedLocal, "https://example.com/", false)]
    [InlineData(BrowserSecurityProfile.TrustedLocal, "about:blank", true)]
    [InlineData(BrowserSecurityProfile.RemoteWeb, "https://example.com/", true)]
    [InlineData(BrowserSecurityProfile.RemoteWeb, "http://example.com/", true)]
    [InlineData(BrowserSecurityProfile.RemoteWeb, "mnvt://game/index.html", false)]
    [InlineData(BrowserSecurityProfile.RemoteWeb, "file:///C:/Windows/win.ini", false)]
    [InlineData(BrowserSecurityProfile.RemoteWeb, "javascript:alert(1)", false)]
    [InlineData(BrowserSecurityProfile.RemoteWeb, "chrome://settings", false)]
    [InlineData(BrowserSecurityProfile.Restricted, "https://example.com/", false)]
    [InlineData(BrowserSecurityProfile.Restricted, "data:text/html,x", false)]
    public void Navigation_FollowsTheProfile(BrowserSecurityProfile profile, string url, bool allowed) =>
        Assert.Equal(allowed, Policy(profile).IsNavigationAllowed(url));

    [Fact]
    public void Allowlist_MatchesHostsAndSubdomainsOnly()
    {
        var restricted = Policy(BrowserSecurityProfile.Restricted, BrowserCapabilities.None, "example.com");
        Assert.True(restricted.IsNavigationAllowed("https://example.com/a"));
        Assert.True(restricted.IsNavigationAllowed("https://docs.example.com/"));
        Assert.False(restricted.IsNavigationAllowed("https://evilexample.com/"));
        Assert.False(restricted.IsNavigationAllowed("https://example.com.evil.net/"));
        Assert.False(restricted.IsNavigationAllowed("http://example.com/")); // restricted: https only
        Assert.True(Policy(BrowserSecurityProfile.RemoteWeb, BrowserCapabilities.None, "example.com").IsNavigationAllowed("http://example.com/"));
        Assert.False(Policy(BrowserSecurityProfile.RemoteWeb, BrowserCapabilities.None, "example.com").IsNavigationAllowed("https://other.org/"));
    }

    [Fact]
    public void Bridge_NeedsCapabilities_AndATrustedOrigin()
    {
        Assert.False(Policy(BrowserSecurityProfile.TrustedLocal).IsBridgeAllowed("mnvt://game/index.html"));
        Assert.True(Policy(BrowserSecurityProfile.TrustedLocal, BrowserCapabilities.Messages).IsBridgeAllowed("mnvt://game/index.html"));
        Assert.False(Policy(BrowserSecurityProfile.TrustedLocal, BrowserCapabilities.Messages).IsBridgeAllowed("https://example.com/"));
        Assert.True(Policy(BrowserSecurityProfile.TrustedLocal, BrowserCapabilities.Messages, "example.com").IsBridgeAllowed("https://example.com/"));
        Assert.False(Policy(BrowserSecurityProfile.RemoteWeb, BrowserCapabilities.Messages).IsBridgeAllowed("https://example.com/"));
        Assert.True(Policy(BrowserSecurityProfile.RemoteWeb, BrowserCapabilities.Messages | BrowserCapabilities.RemoteBridge).IsBridgeAllowed("https://example.com/"));
        Assert.False(Policy(BrowserSecurityProfile.Restricted, BrowserCapabilities.Messages | BrowserCapabilities.RemoteBridge, "example.com").IsBridgeAllowed("https://example.com/"));
        Assert.False(Policy(BrowserSecurityProfile.TrustedLocal, BrowserCapabilities.Messages).IsBridgeAllowed("data:text/html,x"));
    }

    [Fact]
    public void Subresources_OfGameUi_StayLocal()
    {
        var ui = Policy(BrowserSecurityProfile.TrustedLocal, BrowserCapabilities.Messages);
        Assert.True(ui.IsSubresourceAllowed("mnvt://game/app.js"));
        Assert.True(ui.IsSubresourceAllowed("data:image/png;base64,AA=="));
        Assert.False(ui.IsSubresourceAllowed("https://cdn.example.com/lib.js")); // a remote script next to the bridge
        Assert.True(Policy(BrowserSecurityProfile.RemoteWeb).IsSubresourceAllowed("https://cdn.example.com/lib.js"));
        Assert.False(Policy(BrowserSecurityProfile.RemoteWeb).IsSubresourceAllowed("mnvt://game/app.js"));
        Assert.False(Policy(BrowserSecurityProfile.Restricted, BrowserCapabilities.None, "example.com").IsSubresourceAllowed("https://tracker.net/x.js"));
        Assert.False(Policy(BrowserSecurityProfile.RemoteWeb).IsSubresourceAllowed("file:///C:/x.js"));
    }
}

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class SharedFrameTests
{
    [Fact]
    public void DirtyRectangles_Merge_AreSwizzled_AndClearOnTake()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Named shared memory is Windows-only.");
        }

        var name = "mnvt-test-" + Guid.NewGuid().ToString("N");
        using var host = SharedFrame.Create(name, 8, 4);
        using var game = SharedFrame.Open(name, 8, 4)!;
        var bgra = new uint[8 * 4];
        Array.Fill(bgra, 0xFF112233u); // A=FF R=11 G=22 B=33
        var handle = GCHandle.Alloc(bgra, GCHandleType.Pinned);
        try
        {
            host.Write(handle.AddrOfPinnedObject(), 8, 1, 1, 2, 1, unpremultiply: false);
            host.Write(handle.AddrOfPinnedObject(), 8, 5, 2, 1, 2, unpremultiply: false);
        }
        finally
        {
            handle.Free();
        }

        var buffer = new byte[4];
        Assert.True(game.TryTake(ref buffer, out var dirty, out var frames));
        Assert.Equal((1, 1, 5, 3), dirty); // the union of both writes
        Assert.Equal(2, frames);
        Assert.True(buffer.Length >= 5 * 3 * 4);
        Assert.Equal(new byte[] { 0x11, 0x22, 0x33, 0xFF }, buffer[..4]); // RGBA
        Assert.False(game.TryTake(ref buffer, out _, out _));
    }

    [Fact]
    public void TransparentPixels_AreUnpremultiplied()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Named shared memory is Windows-only.");
        }

        var name = "mnvt-test-" + Guid.NewGuid().ToString("N");
        using var host = SharedFrame.Create(name, 1, 1);
        using var game = SharedFrame.Open(name, 1, 1)!;
        var bgra = new uint[] { 0x80004080u }; // premultiplied: A=0x80, R=0x00, G=0x40, B=0x80
        var handle = GCHandle.Alloc(bgra, GCHandleType.Pinned);
        try
        {
            host.Write(handle.AddrOfPinnedObject(), 1, 0, 0, 1, 1, unpremultiply: true);
        }
        finally
        {
            handle.Free();
        }

        var buffer = new byte[4];
        Assert.True(game.TryTake(ref buffer, out _, out _));
        Assert.Equal(new byte[] { 0x00, 0x7F, 0xFF, 0x80 }, buffer);
    }

    [Fact]
    public void OpeningAMissingFrame_ReturnsNull()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Named shared memory is Windows-only.");
        }

        Assert.Null(SharedFrame.Open("mnvt-test-missing-" + Guid.NewGuid().ToString("N"), 4, 4));
    }
}

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class SharedAudioTests
{
    [Fact]
    public unsafe void Frames_ArriveInterleaved_AndAReaderThatFallsBehindSkipsAhead()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Named shared memory is Windows-only.");
        }

        var name = "mnvt-test-" + Guid.NewGuid().ToString("N");
        using var host = SharedAudio.Create(name, 2, 1000, seconds: 1.024f); // 1024 frames
        using var game = SharedAudio.Open(name)!;
        Assert.Equal((2, 1000, 1024), (game.Channels, game.SampleRate, game.CapacityFrames));

        var left = new float[] { 0.5f, -1f, 2f };
        var right = new float[] { 0f, 0.25f, -2f };
        var buffer = new short[4096];
        fixed (float* l = left, r = right)
        {
            var planes = stackalloc float*[] { l, r };
            host.Write((nint)planes, 3);
            Assert.Equal(3, game.Read(buffer));
            Assert.Equal(new short[] { 16383, 0, -32767, 8191, 32767, -32767 }, buffer[..6]); // clamped to -1..1
            Assert.Equal(0, game.Read(buffer));

            for (var i = 0; i < 400; i++)
            {
                host.Write((nint)planes, 3); // 1200 frames: more than the ring holds
            }
        }

        Assert.Equal(1024, game.Read(buffer)); // the newest ring's worth, the rest skipped
    }
}

public sealed class ProtocolTests
{
    [Fact]
    public async Task Messages_RoundTripThroughAPipe()
    {
        var name = "mnvt-test-" + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, BrowserProtocol.PipeBufferBytes, BrowserProtocol.PipeBufferBytes);
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        var accepted = server.WaitForConnectionAsync(TestContext.Current.CancellationToken);
        client.Connect(5000);
        await accepted;
        var message = BrowserProtocol.New(BrowserProtocol.Navigate, 7);
        message["url"] = "mnvt://game/ü.html";
        await BrowserProtocol.WriteAsync(server, message, TestContext.Current.CancellationToken);
        var read = await BrowserProtocol.ReadAsync(client, TestContext.Current.CancellationToken);
        Assert.True(JToken.DeepEquals(message, read));

        server.Dispose();
        Assert.Null(await BrowserProtocol.ReadAsync(client, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OversizedLengths_AreRejected()
    {
        var name = "mnvt-test-" + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, BrowserProtocol.PipeBufferBytes, BrowserProtocol.PipeBufferBytes);
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        var accepted = server.WaitForConnectionAsync(TestContext.Current.CancellationToken);
        client.Connect(5000);
        await accepted;
        await server.WriteAsync(BitConverter.GetBytes(int.MaxValue), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidDataException>(() => BrowserProtocol.ReadAsync(client, TestContext.Current.CancellationToken));
    }
}

public sealed class SurfaceMappingTests
{
    [Fact]
    public void QuadHits_GiveInterpolatedUVs()
    {
        // A unit quad in XY facing +Z, UV (0,0) bottom left.
        var quad = new MeshData(
            [new(-0.5f, -0.5f, 0f), new(0.5f, -0.5f, 0f), new(0.5f, 0.5f, 0f), new(-0.5f, 0.5f, 0f)],
            [0, 1, 2, 0, 2, 3],
            [new(0f, 0f), new(1f, 0f), new(1f, 1f), new(0f, 1f)]);
        var hit = MeshRaycast.HitUV(quad, new Vector3(0.25f, -0.25f, 5f), new Vector3(0f, 0f, -1f));
        Assert.NotNull(hit);
        Assert.Equal(5f, hit.Value.Distance, 3);
        Assert.Equal(0.75f, hit.Value.UV.X, 3);
        Assert.Equal(0.25f, hit.Value.UV.Y, 3);
        Assert.Null(MeshRaycast.HitUV(quad, new Vector3(2f, 0f, 5f), new Vector3(0f, 0f, -1f))); // beside
        Assert.Null(MeshRaycast.HitUV(quad, new Vector3(0f, 0f, -5f), new Vector3(0f, 0f, 1f))); // from behind, single-sided
        Assert.NotNull(MeshRaycast.HitUV(quad, new Vector3(0f, 0f, -5f), new Vector3(0f, 0f, 1f), doubleSided: true));
        Assert.Null(MeshRaycast.HitUV(quad with { TexCoords = null }, new Vector3(0f, 0f, 5f), new Vector3(0f, 0f, -1f)));
    }

    [Fact]
    public void NearestTriangle_Wins()
    {
        var two = new MeshData(
            [new(-1f, -1f, 0f), new(1f, -1f, 0f), new(0f, 1f, 0f), new(-1f, -1f, 1f), new(1f, -1f, 1f), new(0f, 1f, 1f)],
            [0, 1, 2, 3, 4, 5],
            [new(0f, 0f), new(0f, 0f), new(0f, 0f), new(1f, 1f), new(1f, 1f), new(1f, 1f)]);
        var hit = MeshRaycast.HitUV(two, new Vector3(0f, 0f, 5f), new Vector3(0f, 0f, -1f));
        Assert.Equal(new Vector2(1f, 1f), hit!.Value.UV);
    }

    [Theory]
    [InlineData(0f, 0f, 0.5f, 0.5f)]
    [InlineData(-2f, 1f, 0f, 0f)] // top left corner (v grows downwards)
    [InlineData(2f, -1f, 1f, 1f)]
    [InlineData(3f, 0f, 1.25f, 0.5f)] // off the sprite
    public void SpritePoints_MapToPageUVs(float x, float y, float u, float v)
    {
        var sprite = new Scene().CreateGameObject().AddComponent<SpriteRenderer>();
        sprite.Size = new Vector2(4f, 2f);
        var uv = BrowserSurface2D.SpriteUV(sprite, new Vector2(x, y));
        Assert.Equal(u, uv.X, 3);
        Assert.Equal(v, uv.Y, 3);
    }

    [Fact]
    public void SpriteMapping_FollowsRotationScaleAndFlip()
    {
        var sprite = new Scene().CreateGameObject().AddComponent<SpriteRenderer>();
        sprite.Size = new Vector2(4f, 2f);
        sprite.Transform.Position = new Vector3(10f, 0f, 0f);
        sprite.Transform.Rotation2D = 90f;
        sprite.Transform.LocalScale = new Vector3(2f, 2f, 1f);
        // Rotated 90 degrees counter-clockwise and doubled: the page's top left (-4, 2) is now at (-2, -4) from the center.
        var corner = BrowserSurface2D.SpriteUV(sprite, new Vector2(10f - 2f, -4f));
        Assert.Equal(0f, corner.X, 3);
        Assert.Equal(0f, corner.Y, 3);
        sprite.FlipX = true;
        Assert.Equal(1f, BrowserSurface2D.SpriteUV(sprite, new Vector2(8f, -4f)).X, 3);
    }

    [Fact]
    public void Keys_MapToWindowsVirtualKeys()
    {
        Assert.Equal(0x41, BrowserInput.VirtualKey(Key.A));
        Assert.Equal(0x5A, BrowserInput.VirtualKey(Key.Z));
        Assert.Equal(0x30, BrowserInput.VirtualKey(Key.Number0));
        Assert.Equal(0x7B, BrowserInput.VirtualKey(Key.F12));
        Assert.Equal(0x0D, BrowserInput.VirtualKey(Key.Enter));
        Assert.Equal(0x10, BrowserInput.VirtualKey(Key.ShiftRight));
        Assert.Equal(0, BrowserInput.VirtualKey(Key.Unknown));
        Assert.All(Enum.GetValues<Key>().Where(k => k != Key.Unknown), k => Assert.NotEqual(0, BrowserInput.VirtualKey(k)));
    }

    [Fact]
    public void SurfaceSettings_RoundTripThroughTheSceneSerializer()
    {
        var registry = ComponentRegistry.CreateDefault();
        new Browser2DModule().Register(registry);
        registry.Register<SpriteRenderer>();
        var scene = new Scene("S");
        var surface = scene.CreateGameObject("Page").AddComponent<BrowserSurface2D>();
        surface.Url = "mnvt://game/menu.html";
        (surface.PageWidth, surface.PageHeight, surface.Transparent, surface.FrameRate) = (800, 600, true, 24);
        (surface.Profile, surface.Capabilities, surface.Muted, surface.Interactive) = (BrowserSecurityProfile.Restricted, BrowserCapabilities.Messages | BrowserCapabilities.Requests, true, false);
        surface.AllowedDomains = ["example.com"];
        var serializer = new SceneSerializer(registry);
        var loaded = serializer.Deserialize(serializer.Serialize(scene)).Scene.Find("Page")!.GetComponent<BrowserSurface2D>()!;
        Assert.Equal("mnvt://game/menu.html", loaded.Url);
        Assert.Equal((800, 600, true, 24), (loaded.PageWidth, loaded.PageHeight, loaded.Transparent, loaded.FrameRate));
        Assert.Equal((BrowserSecurityProfile.Restricted, BrowserCapabilities.Messages | BrowserCapabilities.Requests, true, false), (loaded.Profile, loaded.Capabilities, loaded.Muted, loaded.Interactive));
        Assert.Equal(["example.com"], loaded.AllowedDomains);
    }
}
