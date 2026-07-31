using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Andy.Cli.Auth;

/// <summary>
/// The local callback endpoint for the OAuth authorization-code flow.
///
/// SECURITY (issue #284): the listener binds to 127.0.0.1 only - never to 0.0.0.0 or a
/// hostname - so no other machine can deliver a callback. The <c>state</c> parameter is
/// compared in constant time before the authorization code is accepted; a mismatched or
/// missing state is answered with HTTP 400 and surfaces as an <see cref="OAuthException"/>.
/// The authorization code is never written to the HTML response, a log, or an exception.
/// </summary>
public sealed class LoopbackOAuthListener : IDisposable
{
    private readonly HttpListener _listener;
    private bool _disposed;

    private LoopbackOAuthListener(HttpListener listener, string redirectUri)
    {
        _listener = listener;
        RedirectUri = redirectUri;
    }

    /// <summary>The loopback redirect URI the authorization request must use.</summary>
    public string RedirectUri { get; }

    /// <summary>
    /// Binds the callback listener. <paramref name="port"/> zero picks a free ephemeral port.
    /// </summary>
    public static LoopbackOAuthListener Start(int port, string callbackPath)
    {
        var path = NormalizePath(callbackPath);
        var chosenPort = port > 0 ? port : FindFreeLoopbackPort();

        // The prefix is pinned to the loopback literal so the listener can never be reached
        // from another host, even if the machine has a public interface.
        var prefix = $"http://127.0.0.1:{chosenPort}{path}/";
        var listener = new HttpListener();
        listener.Prefixes.Add(prefix);

        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            listener.Close();
            throw new OAuthException(
                $"Could not bind the local OAuth callback on 127.0.0.1:{chosenPort}. "
                + "Choose a different callbackPort, or use the device-code login instead.", ex);
        }

        return new LoopbackOAuthListener(listener, $"http://127.0.0.1:{chosenPort}{path}");
    }

    /// <summary>
    /// Waits for the provider to redirect the browser back here and returns the authorization
    /// code. Throws <see cref="OAuthException"/> on a state mismatch, on a provider-reported
    /// error, or on timeout; throws <see cref="OperationCanceledException"/> when the user
    /// cancels.
    /// </summary>
    public async Task<string> WaitForAuthorizationCodeAsync(
        string expectedState,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        while (true)
        {
            HttpListenerContext context;
            try
            {
                var contextTask = _listener.GetContextAsync();
                var completed = await Task.WhenAny(
                    contextTask,
                    Task.Delay(Timeout.Infinite, linked.Token)).ConfigureAwait(false);

                if (completed != contextTask)
                {
                    // Distinguish "user pressed Esc" from "the login window was never finished".
                    if (cancellationToken.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(cancellationToken);
                    }

                    throw new OAuthException(
                        $"Timed out after {timeout.TotalSeconds:0} seconds waiting for the provider to "
                        + "redirect back to andy-cli. No credential was stored.");
                }

                context = await contextTask.ConfigureAwait(false);
            }
            catch (HttpListenerException ex)
            {
                throw new OAuthException("The local OAuth callback listener stopped unexpectedly.", ex);
            }
            catch (ObjectDisposedException ex)
            {
                throw new OAuthException("The local OAuth callback listener was closed.", ex);
            }

            var query = context.Request.QueryString;
            var error = query["error"];
            var state = query["state"];
            var code = query["code"];

            if (!string.IsNullOrEmpty(error))
            {
                // error_description is provider-controlled text; it is shown as-is but can
                // never contain our secrets because none were sent to the browser.
                var description = query["error_description"];
                Respond(context, 400, "Login failed", "You can close this window and return to andy-cli.");
                throw new OAuthException(
                    $"The provider rejected the login ({error}){(string.IsNullOrEmpty(description) ? "." : ": " + description)}");
            }

            if (!OAuthSecurity.StateMatches(expectedState, state))
            {
                // Do not stop listening: an unrelated or replayed request must not be able to
                // abort a login the user is still completing in the browser.
                Respond(context, 400, "Request rejected", "The login request could not be verified. Start the login again from andy-cli.");
                throw new OAuthException(
                    "The OAuth callback did not carry the expected state value, so the authorization "
                    + "code was rejected. No credential was stored. Start the login again.");
            }

            if (string.IsNullOrEmpty(code))
            {
                Respond(context, 400, "Login incomplete", "No authorization code was returned. Start the login again from andy-cli.");
                throw new OAuthException("The OAuth callback did not include an authorization code.");
            }

            Respond(context, 200, "Login complete", "You can close this window and return to andy-cli.");
            return code;
        }
    }

    private static void Respond(HttpListenerContext context, int statusCode, string title, string message)
    {
        try
        {
            var body = Encoding.UTF8.GetBytes(
                "<!doctype html><html><head><meta charset=\"utf-8\"><title>andy-cli</title></head>"
                + $"<body style=\"font-family:system-ui;margin:3rem\"><h1>{WebUtility.HtmlEncode(title)}</h1>"
                + $"<p>{WebUtility.HtmlEncode(message)}</p></body></html>");

            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = body.Length;
            context.Response.OutputStream.Write(body, 0, body.Length);
            context.Response.OutputStream.Close();
        }
        catch (Exception)
        {
            // The browser may have gone away; the flow result does not depend on the response.
        }
    }

    private static string NormalizePath(string callbackPath)
    {
        var path = string.IsNullOrWhiteSpace(callbackPath) ? "/andy-cli/callback" : callbackPath.Trim();
        if (!path.StartsWith('/'))
        {
            path = "/" + path;
        }

        return path.TrimEnd('/');
    }

    private static int FindFreeLoopbackPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try
        {
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _listener.Close();
        }
        catch (Exception)
        {
            // Nothing useful to do while tearing down.
        }
    }
}
