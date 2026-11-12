using MNVTEngine;
using MNVTEngine.Browser;

namespace Browser2D;

/// <summary>
/// Talks to the page on this GameObject's <see cref="BrowserSurface2D"/> (<c>Assets/Web/index.html</c>): answers its
/// <c>gameApi.request("stats")</c>, counts its "clicked" messages and posts the count back, and recovers when the page's
/// renderer crashes. F5 reloads the page, F6 crashes its renderer (to try the recovery), F7 runs JavaScript from C#.
/// </summary>
public sealed class BrowserDemo : Component
{
    private BrowserInstance? _browser;
    private int _clicks;
    private float _time;

    public override void Update()
    {
        _time += Time.DeltaTime;
        if (_browser is null && GetComponent<BrowserSurface2D>()?.Browser is { } browser)
        {
            Attach(browser);
        }

        if (_browser is null)
        {
            return;
        }

        if (Input.GetKeyDown(Key.F5))
        {
            _browser.Reload();
        }

        if (Input.GetKeyDown(Key.F6))
        {
            _browser.SimulateRendererCrash();
        }

        if (Input.GetKeyDown(Key.F7))
        {
            _ = ShowTitleAsync(_browser);
        }
    }

    private void Attach(BrowserInstance browser)
    {
        _browser = browser;
        browser.RegisterRequestHandler("stats", _ => new { clicks = _clicks, seconds = Math.Round(_time, 1), engine = "MNVTEngine" });
        browser.MessageReceived += message =>
        {
            if (message.Name == "clicked")
            {
                _clicks++;
                browser.PostMessage("score", new { clicks = _clicks, from = message.DataAs<string>() });
            }
            else if (message.Name == "submitted")
            {
                Log.Info($"The page sent: {message.Data}");
            }
        };
        browser.ConsoleMessage += line => Log.Info($"[page {line.Level}] {line.Message}");
        browser.LoadCompleted += (url, status) => Log.Info($"Loaded {url} ({status})");
        browser.Blocked += (what, url) => Log.Warn($"Blocked {what}: {url}");
        browser.Crashed += reason =>
        {
            Log.Warn($"Browser crashed: {reason}. Reloading.");
            browser.Reload();
        };
    }

    private async Task ShowTitleAsync(BrowserInstance browser)
    {
        try
        {
            var title = await browser.ExecuteJavaScriptAsync("document.title + ' / ' + document.querySelectorAll('button').length + ' buttons'");
            Log.Info($"JavaScript says: {title}");
        }
        catch (BrowserScriptException ex)
        {
            Log.Warn($"JavaScript failed: {ex.Message}");
        }
    }
}
