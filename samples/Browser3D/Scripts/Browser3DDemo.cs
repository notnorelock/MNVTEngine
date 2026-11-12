using MNVTEngine;
using MNVTEngine.Browser;
using MNVTEngine.Mathematics;

namespace Browser3D;

/// <summary>
/// The page on the monitor (<see cref="BrowserSurface3D"/>): counts its "clicked" messages, answers
/// <c>gameApi.request("stats")</c> and reloads after a renderer crash. F5 reloads, F6 crashes the renderer.
/// </summary>
public sealed class MonitorDemo : Component
{
    private BrowserInstance? _browser;
    private int _clicks;

    public override void Update()
    {
        if (_browser is null && GetComponent<BrowserSurface3D>()?.Browser is { } browser)
        {
            _browser = browser;
            browser.RegisterRequestHandler("stats", _ => new { clicks = _clicks, scene = GameObject.Scene.Name, engine = "MNVTEngine 3D" });
            browser.MessageReceived += message =>
            {
                if (message.Name == "clicked")
                {
                    _clicks++;
                    browser.PostMessage("score", new { clicks = _clicks });
                }
            };
            browser.LoadCompleted += (url, status) => Log.Info($"Loaded {url} ({status})");
            browser.Crashed += reason =>
            {
                Log.Warn($"Browser crashed: {reason}. Reloading.");
                browser.Reload();
            };
        }

        if (Input.GetKeyDown(Key.F5))
        {
            _browser?.Reload();
        }

        if (Input.GetKeyDown(Key.F6))
        {
            _browser?.SimulateRendererCrash();
        }
    }
}

/// <summary>Turns its GameObject about Y: the page on the cube's faces keeps answering the mouse while it moves.</summary>
public sealed class Spinner : Component
{
    public float DegreesPerSecond = 20f;

    public override void Update() => Transform.Rotate(new Vector3(0f, DegreesPerSecond * Time.DeltaTime, 0f));
}
