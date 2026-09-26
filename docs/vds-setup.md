# Arch VDS setup for enclosure.crazypenguin.net

This guide targets your **Arch Linux VDS (x86_64)** and **enclosure.crazypenguin.net**, with a public IPv4 address and SSH access with sudo. It runs one CPU-only game server, supervised by systemd, behind Caddy for automatic HTTPS. Your computer and the temporary Cloudflare tunnel are no longer needed once the VDS is serving the domain.

The included templates are [enclosed-ai.service](../deploy/vds/enclosed-ai.service) and [Caddyfile](../deploy/vds/Caddyfile). The Caddyfile already contains your hostname. These are setup files; creating them has not changed any VDS, DNS record, or the current public tunnel.

**Existing web-server check:** on September 26, 2026, the domain resolved to `152.53.38.183`, HTTP responded with `Server: Apache` and redirected to HTTPS, and HTTPS certificate validation failed because its certificate did not match `enclosure.crazypenguin.net`. Confirm that this is your VDS. If Apache already serves websites there, add this game to that existing proxy/hosting setup instead of starting Caddy on the same ports or replacing its configuration. You can inspect listeners on the VDS with `sudo ss -ltnp`. The Caddy steps below apply when Caddy is the chosen front-end web server; the application/runtime/systemd setup is the same with either proxy.

## 1. Point the subdomain at the VDS

Use your existing subdomain, **enclosure.crazypenguin.net**. No additional domain purchase is needed.

In the **crazypenguin.net** DNS zone, the required record is:

| Type | Name | Target |
| --- | --- | --- |
| A | `enclosure` | Your VDS public IPv4 address |

An A record currently points to `152.53.38.183`; if that is your VDS IP, keep it. The lookup returned no AAAA or CNAME record for this subdomain. Leave the parent domain's `@` and `www` records unchanged. This setup uses only `enclosure.crazypenguin.net`, without a `www.enclosure.crazypenguin.net` alias.

Use an AAAA record only if IPv6 is also correctly routed to this server. If DNS is managed by Cloudflare, use **DNS only** for this direct-to-Caddy setup. The custom domain replaces the randomly assigned Quick Tunnel hostname; renaming that temporary hostname is not the setup method. [Cloudflare Quick Tunnels](https://developers.cloudflare.com/cloudflare-one/networks/connectors/cloudflare-tunnel/do-more-with-tunnels/trycloudflare/).

Allow inbound **TCP 80 and 443** in the VDS provider's firewall and the server firewall, and retain access to your SSH port. UDP 443 is optional for HTTP/3. The game port 5081 remains on loopback and does not need to be public. Caddy obtains and renews certificates once DNS and access to ports 80/443 are correct. [Caddy HTTPS requirements](https://caddyserver.com/docs/automatic-https#overview).

## 2. Install the runtime and Caddy on the VDS

SSH into the VDS and run:

```sh
sudo pacman -Syu --needed aspnet-runtime-10.0 caddy curl nano
dotnet --list-runtimes
```

The runtime list should include **both** `Microsoft.AspNetCore.App 10.x` and `Microsoft.NETCore.App 10.x`. The versioned `aspnet-runtime-10.0` package installs the required .NET runtime dependency. The SDK, TorchSharp, and CUDA are not needed on the VDS; only the Web project is deployed. [Arch's ASP.NET Core 10 package](https://archlinux.org/packages/extra/x86_64/aspnet-runtime-10.0/).

`-Syu` updates the Arch system as well as installing the packages. Both the runtime and Caddy are available in Arch's official Extra repository; no AUR helper or Debian package repository is needed. Caddy's Arch package includes its systemd service, which is enabled after configuration in step 5. These commands assume Caddy will own ports 80 and 443. If another website already uses those ports, integrate with its existing reverse proxy instead of starting a competing listener. [Caddy's Arch installation notes](https://caddyserver.com/docs/install#arch-linux-manjaro-parabola).

## 3. Build and upload from your development computer

Run these commands **in the repository on your computer**, replacing the SSH destination:

```sh
game_publish=$(mktemp -d /tmp/enclosed-ai-release.XXXXXX)
dotnet publish StratJamAI.Web/StratJamAI.Web.csproj -c Release -o "$game_publish"
tar -C "$game_publish" -czf /tmp/enclosed-ai-web.tar.gz .
scp /tmp/enclosed-ai-web.tar.gz YOUR_SSH_USER@YOUR_VDS_IP:/tmp/
scp deploy/vds/enclosed-ai.service deploy/vds/Caddyfile YOUR_SSH_USER@YOUR_VDS_IP:/tmp/
```

Build only `StratJamAI.Web`, not the training CLI. Keep the entire publish output, including `wwwroot`, together. This framework-dependent output uses the runtime installed in step 2.

## 4. Install the application and start its service

Back on the **VDS**, create the unprivileged service account once and unpack the first release:

```sh
sudo useradd --system --user-group --home-dir /opt/enclosed-ai --no-create-home --shell /usr/bin/nologin enclosed-ai
sudo install -d -m 755 /opt/enclosed-ai/releases/initial
sudo tar --no-same-owner -xzf /tmp/enclosed-ai-web.tar.gz -C /opt/enclosed-ai/releases/initial
sudo chmod -R a+rX /opt/enclosed-ai/releases/initial
sudo ln -s /opt/enclosed-ai/releases/initial /opt/enclosed-ai/current
sudo install -m 644 /tmp/enclosed-ai.service /etc/systemd/system/enclosed-ai.service
sudo systemctl daemon-reload
sudo systemctl enable --now enclosed-ai
sudo systemctl status enclosed-ai --no-pager
curl --fail http://127.0.0.1:5081/api/info
```

The health response should include `hostingVersion: "pvp-v1"` and `engine: "alpha-beta-defense-v4"`. `publicAccess: false` is expected: Caddy is the public endpoint and the app listens on loopback. A manually launched compatible server on 5081 must be stopped before enabling this service, since the launcher can otherwise reuse it and exit.

The service starts after reboot and restarts after crashes. It allows two simultaneous AI searches across opponents, coaching, and reviews. On a one-vCPU VDS, start with `--max-searches 1`; adjust after observing your machine's load. PvP itself does not run the AI. The 128-session setting is a configured ceiling, not a measured capacity guarantee.

## 5. Configure the domain and HTTPS

The uploaded template already configures **enclosure.crazypenguin.net**. Validate it before enabling or reloading Caddy. The commands below install the complete file on a dedicated Caddy host. If Caddy already serves other websites, merge only the `enclosure.crazypenguin.net` site block into its existing configuration and validate that combined file. If Apache or another proxy owns ports 80/443, configure the site there instead; do not run both listeners on those ports.

Keep the template's global `admin "unix//run/caddy/admin.socket"` setting. It matches Arch's packaged Caddy configuration and allows reloads to use the existing protected socket. The Arch package creates the service user and runtime directories; keep its supplied `caddy.service`. [Arch Caddy package](https://archlinux.org/packages/extra/x86_64/caddy/).

```sh
sudo caddy validate --config /tmp/Caddyfile --adapter caddyfile
sudo cp -a /etc/caddy/Caddyfile /etc/caddy/Caddyfile.before-enclosed-ai
sudo install -m 644 /tmp/Caddyfile /etc/caddy/Caddyfile
sudo systemctl enable --now caddy
sudo systemctl reload caddy
```

Once the proxy and certificate are configured, open **https://enclosure.crazypenguin.net/**. The homepage keeps Play AI and the link to multiplayer. **https://enclosure.crazypenguin.net/pvp** is the clean multiplayer address; old `/pvp.html?code=...` invitations redirect with their code preserved. Caddy preserves the browser's Host and supplies the forwarded scheme, which this app trusts from loopback. [Caddy proxy headers](https://caddyserver.com/docs/caddyfile/directives/reverse_proxy#defaults).

Check the actual domain:

```sh
curl --fail https://enclosure.crazypenguin.net/api/info
curl --head https://enclosure.crazypenguin.net/pvp
sudo journalctl -u enclosed-ai -n 50 --no-pager
sudo journalctl -u caddy -n 50 --no-pager
```

Test one AI game and use two different browser sessions for a private PvP room. Keep the old temporary site available until the new domain works. Visiting the new domain creates a new browser identity; old cookies and active games do not transfer. For the original-site coach, reinstall/configure the userscript using **https://enclosure.crazypenguin.net/coach-setup.html** after the new site is serving correctly.

## Updates and restarts

For the next deployment, publish/upload a new archive as in step 3. Extract it into a **new release directory**, not the running one:

```sh
game_release=$(date -u +%Y%m%d-%H%M%S)
sudo install -d -m 755 "/opt/enclosed-ai/releases/$game_release"
sudo tar --no-same-owner -xzf /tmp/enclosed-ai-web.tar.gz -C "/opt/enclosed-ai/releases/$game_release"
sudo chmod -R a+rX "/opt/enclosed-ai/releases/$game_release"
readlink -f /opt/enclosed-ai/current
sudo ln -s "/opt/enclosed-ai/releases/$game_release" /opt/enclosed-ai/current.next
sudo mv -Tf /opt/enclosed-ai/current.next /opt/enclosed-ai/current
sudo systemctl restart enclosed-ai
curl --fail --retry 10 --retry-connrefused --retry-delay 1 http://127.0.0.1:5081/api/info
```

Keep the previous release path printed by `readlink` for rollback: switch `current` back to it and restart the service. A port that is already serving another application must be resolved rather than treating its health response as this release. Use the app's version and features in `/api/info` to check the result.

**Games, lobbies, and reviews are currently stored in memory.** A restart or crash clears them. Automatic startup keeps the site available again, but it does not restore matches. Run one application process; replicas do not share matchmaking or state. Export finished histories before planned maintenance if they must be kept. See [multiplayer lifetime limits](multiplayer.md).

## Configuration checks

The included unit passed `systemd-analyze verify`. Caddy 2.11.4 accepted the template, including the Arch admin-socket setting. The packaged Arch Caddy unit, configuration, and directory permissions were also inspected. An isolated local HTTPS proxy against the running app previously verified the `/pvp` rewrite, preservation of an invitation's query string, Secure/HttpOnly cookies, successful same-origin API mutation, and rejection of a foreign Origin. The test used its own temporary local certificate without installing it into the machine's trust store, and did not restart the public server.

The Arch package names and paths follow the official sources linked above; the installation commands have not been run on your VDS. Your subdomain already exists, but its destination still needs to be confirmed as the VDS, and the application/proxy deployment and a matching public certificate still need to be verified there.
