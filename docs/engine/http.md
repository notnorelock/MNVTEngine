# HTTP client (MNVTEngine.Http)

`HttpApi` calls web APIs from game code: accounts, leaderboards, matchmaking, store backends. It is modelled on
Axios: an instance carries a base URL and defaults, requests and responses are JSON, interceptors run around every
call, and failures come back as values instead of exceptions. It ships with the `networking` module and is headless
(no scenes, graphics or platform), so tools and dedicated servers can use it too.

```csharp
using MNVTEngine.Http;

public sealed class Leaderboard : Component
{
    private HttpApi? _api;

    public override void Start()
    {
        _api = new HttpApi(new HttpOptions { BaseUrl = new Uri("https://api.example.com/v1/") });
        _api.OnRequest(request =>
        {
            request.WithBearer(Session.Token);
            return ValueTask.CompletedTask;
        });
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        var top = await _api!.GetAsync<Score[]>("scores", r => r.Query["limit"] = "10");
        if (!top.Ok)
        {
            Log.Warning(top.ErrorData<ApiError>()?.Message ?? top.Error!.Message);
            return;
        }

        Show(top.Data!); // back on the game thread: Time, Input and UI work here
    }

    public override void OnDestroy() => _api?.Dispose();
}
```

## Requests

- `GetAsync<T>`, `PostAsync<T>`, `PutAsync<T>`, `PatchAsync<T>`, `DeleteAsync<T>` build a request and send it;
  `SendAsync<T>(HttpRequest)` sends one built by hand. The optional `configure` callback edits the request first:
  `Headers`, `Query` (escaped; null values skipped), `Timeout`, `WithBearer(token)`.
- URLs are absolute or relative to `BaseUrl` (a trailing slash is added to it). Only `http` and `https` are sent;
  any other scheme fails without a request.
- Bodies: an object is sent as JSON, a `string` as text, an `HttpContent` as is. A `Content-Type` header replaces the
  default one.
- JSON uses Newtonsoft with camelCase names and `TypeNameHandling.None` (`HttpOptions.Json` can be changed, but never
  enable type names: responses are untrusted input).
- `T = string` returns the raw body.

## Responses

Every call returns `HttpResponse<T>`: `Ok`, `Status`/`StatusCode`, `Headers`, `Text`, `Data` and `Error`. `Error.Kind`
tells failures apart:

| Kind | Meaning |
|---|---|
| `Status` | The server answered outside 200-299. `ErrorData<TError>()` reads the body as the API's error type. |
| `Network` | No answer: DNS, refused or dropped connection, TLS failure, or a URL that is not http(s). |
| `Timeout` | No answer within `Timeout` (30 s by default, per request overridable). |
| `Parse` | A success status whose body is not the expected JSON (`Text` holds it). |
| `TooLarge` | The body exceeded `MaxResponseBytes` (16 MiB by default), checked while reading. |

Only cancellation through the caller's `CancellationToken` throws (`OperationCanceledException`).
`EnsureSuccess()` turns a failure into `HttpRequestFailedException` for code that prefers exceptions.

## Interceptors

`OnRequest` runs before each request in the order added (add auth headers, trace ids); `OnResponse` runs after each
response, failed ones included (sign out on 401, metrics). Add them before sending requests from several threads.

## Threading and lifetime

The client works with `ConfigureAwait(false)` internally. An `await` in a component resumes on the game thread at
the start of the scene's next update through the scene's synchronization context (see
[scenes and runtime](scenes-and-runtime.md)); code outside a scene resumes on the thread pool.

Create one `HttpApi` per service and keep it: it pools connections (refreshed every two minutes, so DNS changes are
picked up). Dispose it when the owner goes away. Tests pass their own `HttpMessageHandler`.
