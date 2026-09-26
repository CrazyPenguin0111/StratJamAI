# Public hosting validation

Validated September 26, 2026. All 116 tests passed: 72 core/CLI/training tests and 44 web tests.

New coverage includes isolated browser games, cryptographic session identifiers, session expiry and capacity, in-flight request leases, cancellation while waiting for shared search slots, concurrency limits, same-origin checks for LAN/HTTPS hosts, older-server rejection, and tunnel process cleanup without stopping a reused server.

Real HTTP checks against `--public` confirmed:

- Two cookie jars keep independent moves and histories; AI work survives request completion.
- `/api/info` does not create game sessions.
- Same-origin LAN and forwarded HTTPS requests work; foreign origins receive 403.
- Game cookies are HttpOnly, SameSite=Strict, and Secure over proxied HTTPS.
- Session capacity returns 503 with Retry-After; excessive per-session requests return 429.

The CLI started a real Cloudflare Quick Tunnel using a checksum-verified official `cloudflared` 2026.9.3 executable. Browser checks use the public HTTPS address with independent browser contexts, so they cover the tunnel, cookies, origin checks, and actual AI responses together.

The browser also has mocked integration checks for session replacement after a server restart, rejecting older revisions within the same session, and honoring Retry-After without immediate repeated requests. Session isolation is per browser cookie, not a login system. Games remain in memory; save/load provides persistence across restarts.

References: [Cloudflare Quick Tunnels](https://developers.cloudflare.com/cloudflare-one/networks/connectors/cloudflare-tunnel/do-more-with-tunnels/trycloudflare/), [ASP.NET Core forwarded headers](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/proxy-load-balancer?view=aspnetcore-10.0), [ASP.NET Core rate limiting](https://learn.microsoft.com/en-us/aspnet/core/performance/rate-limit?view=aspnetcore-10.0).
