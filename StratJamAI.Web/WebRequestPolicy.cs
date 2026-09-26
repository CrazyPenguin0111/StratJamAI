namespace StratJamAI.Web;

public static class WebRequestPolicy
{
    // Use the host the visitor actually opened. Forwarded scheme is accepted only from
    // the local tunnel/reverse proxy, configured separately in Program.
    public static bool AllowsMutation(HttpRequest request)
    {
        if (!HttpMethods.IsPost(request.Method)) return true;
        if (request.Headers["Sec-Fetch-Site"] == "cross-site") return false;
        if (!request.Headers.ContainsKey("Origin")) return true; // Non-browser API clients.
        if (request.Headers.Origin.Count != 1 ||
            !Uri.TryCreate(request.Headers.Origin.ToString(), UriKind.Absolute, out var origin) ||
            origin.Scheme is not ("http" or "https") || origin.UserInfo.Length != 0 ||
            origin.AbsolutePath != "/" || origin.Query.Length != 0 || origin.Fragment.Length != 0)
            return false;
        var expectedPort = request.Host.Port ?? (request.IsHttps ? 443 : 80);
        return string.Equals(origin.Scheme, request.Scheme, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(origin.Host, request.Host.Host, StringComparison.OrdinalIgnoreCase) && origin.Port == expectedPort;
    }
}
