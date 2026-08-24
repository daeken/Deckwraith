using System.Net;
using System.Security.Cryptography;
using System.Text;
using Deckwraith.Providers.Abstractions;

namespace Deckwraith.Providers.OpenAI;

public sealed record OpenAiSubscriptionBrowserLoginOptions(
    int CallbackPort = 1455,
    int TimeoutSeconds = 300);

internal sealed record OpenAiSubscriptionAuthorizationRequest(
    Uri AuthorizationUri,
    Uri RedirectUri,
    string State,
    string CodeVerifier);

internal sealed class OpenAiSubscriptionBrowserLogin(
    OpenAiSubscriptionCredentialManager credentials)
{
    private static readonly SemaphoreSlim LoginGate = new(1, 1);
    private readonly OpenAiSubscriptionCredentialManager _credentials =
        credentials ?? throw new ArgumentNullException(nameof(credentials));

    public async ValueTask<ProviderAuthenticationStatus> SignInAsync(
        Func<Uri, CancellationToken, ValueTask> openBrowser,
        OpenAiSubscriptionBrowserLoginOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(openBrowser);
        options ??= new OpenAiSubscriptionBrowserLoginOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(options.CallbackPort, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.CallbackPort, ushort.MaxValue);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.TimeoutSeconds);
        if (!await LoginGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new OpenAiAuthenticationException(
                "credential-login-active",
                "A ChatGPT sign-in is already in progress.",
                false);
        }

        try
        {
            var redirectUri = new Uri(
                $"http://localhost:{options.CallbackPort}/auth/callback");
            var authorization = _credentials.CreateAuthorizationRequest(redirectUri);
            using var listener = new HttpListener
            {
                IgnoreWriteExceptions = true,
            };
            listener.Prefixes.Add($"http://localhost:{options.CallbackPort}/");
            try
            {
                listener.Start();
            }
            catch (Exception exception) when (
                exception is HttpListenerException or InvalidOperationException)
            {
                throw new OpenAiAuthenticationException(
                    "credential-login-listener",
                    $"Deckwraith could not reserve localhost:{options.CallbackPort} for ChatGPT sign-in.",
                    true,
                    exception);
            }

            try
            {
                try
                {
                    await openBrowser(authorization.AuthorizationUri, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    throw new OpenAiAuthenticationException(
                        "credential-login-browser",
                        "Deckwraith could not open the ChatGPT sign-in page.",
                        true,
                        exception);
                }

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
                while (true)
                {
                    HttpListenerContext callback;
                    try
                    {
                        callback = await listener.GetContextAsync()
                            .WaitAsync(timeout.Token)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException exception)
                        when (!cancellationToken.IsCancellationRequested)
                    {
                        throw new OpenAiAuthenticationException(
                            "credential-login-timeout",
                            "ChatGPT sign-in timed out. Try connecting again.",
                            true,
                            exception);
                    }

                    if (!StringComparer.Ordinal.Equals(
                            callback.Request.HttpMethod,
                            HttpMethod.Get.Method) ||
                        !StringComparer.Ordinal.Equals(
                            callback.Request.Url?.AbsolutePath,
                            authorization.RedirectUri.AbsolutePath))
                    {
                        await RespondAsync(callback.Response, success: false, notFound: true)
                            .ConfigureAwait(false);
                        continue;
                    }

                    var returnedState = callback.Request.QueryString["state"];
                    if (!StatesMatch(authorization.State, returnedState))
                    {
                        await RespondAsync(callback.Response, success: false)
                            .ConfigureAwait(false);
                        throw new OpenAiAuthenticationException(
                            "credential-login-state",
                            "ChatGPT sign-in returned an invalid security state. Try connecting again.",
                            false);
                    }

                    var error = callback.Request.QueryString["error"];
                    if (!string.IsNullOrWhiteSpace(error))
                    {
                        await RespondAsync(callback.Response, success: false)
                            .ConfigureAwait(false);
                        throw new OpenAiAuthenticationException(
                            "credential-login-rejected",
                            "ChatGPT sign-in was cancelled or rejected.",
                            false);
                    }

                    var code = callback.Request.QueryString["code"];
                    if (string.IsNullOrWhiteSpace(code))
                    {
                        await RespondAsync(callback.Response, success: false)
                            .ConfigureAwait(false);
                        throw new OpenAiAuthenticationException(
                            "credential-login-code-missing",
                            "ChatGPT sign-in returned no authorization code.",
                            false);
                    }

                    try
                    {
                        var status = await _credentials.ExchangeAuthorizationCodeAsync(
                            authorization,
                            code,
                            cancellationToken).ConfigureAwait(false);
                        await RespondAsync(callback.Response, success: true).ConfigureAwait(false);
                        return status;
                    }
                    catch
                    {
                        await RespondAsync(callback.Response, success: false).ConfigureAwait(false);
                        throw;
                    }
                }
            }
            finally
            {
                listener.Stop();
            }
        }
        finally
        {
            LoginGate.Release();
        }
    }

    private static bool StatesMatch(string expected, string? actual)
    {
        if (actual is null)
        {
            return false;
        }

        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var actualBytes = Encoding.UTF8.GetBytes(actual);
        return expectedBytes.Length == actualBytes.Length &&
            CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }

    private static async Task RespondAsync(
        HttpListenerResponse response,
        bool success,
        bool notFound = false)
    {
        response.StatusCode = notFound
            ? StatusCodes.NotFound
            : StatusCodes.Ok;
        response.ContentType = "text/html; charset=utf-8";
        response.Headers["Content-Security-Policy"] =
            "default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; frame-ancestors 'none'";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers["X-Content-Type-Options"] = "nosniff";
        var title = success ? "Deckwraith is connected" : "Deckwraith could not connect";
        var detail = success
            ? "You can close this tab and return to Deckwraith."
            : notFound
                ? "This callback path is not available."
                : "Return to Deckwraith for details, then try again.";
        var body = Encoding.UTF8.GetBytes($$"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>{{title}}</title>
              <style>
                :root { color-scheme: dark; font-family: ui-sans-serif, system-ui, sans-serif; }
                body { min-height: 100vh; margin: 0; display: grid; place-items: center; background: #090b10; color: #d9dbe3; }
                main { width: min(34rem, calc(100vw - 3rem)); padding: 2rem; border: 1px solid #343743; border-radius: 1rem; background: #14161e; }
                h1 { margin-top: 0; font-family: Georgia, serif; font-weight: 500; }
                p { margin-bottom: 0; color: #898d9b; line-height: 1.6; }
              </style>
            </head>
            <body><main><h1>{{title}}</h1><p>{{detail}}</p></main></body>
            </html>
            """);
        response.ContentLength64 = body.Length;
        try
        {
            await response.OutputStream.WriteAsync(body).ConfigureAwait(false);
        }
        finally
        {
            response.Close();
        }
    }

    private static class StatusCodes
    {
        public const int Ok = 200;
        public const int NotFound = 404;
    }
}
