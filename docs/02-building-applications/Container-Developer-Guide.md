# enList Containers — Developer Guide

**Audience:** anyone working on, testing, or debugging enList's container support.
**Status:** describes what is **implemented and working today** (phases C0–C5 of [`Container-Story.md`](../03-architecture/Container-Story.md), after a full regression review). Every command here was run on Windows 11 + Docker Desktop 29.7.2 (WSL2, Linux containers) on 2026-09-07 and produced the output shown, except the wslc material in §2a, verified against wslc 2.9.11.0. **Revised 2026-09-13** after a regression review: the authorization note in §1a, the TLS requirement in §1c, the ownership labels in §3, and the engine-agnostic timeout in §9. Where something does *not* work, that is stated with the evidence.

For *why* the design is shaped this way, read [`Container-Story.md`](../03-architecture/Container-Story.md). This document is the how.

---

## 1. What works today

| | Status |
|---|---|
| An existing package runs in a container with **no rebuild of the package** | ✅ working |
| Plugin discovery inside the container (services, jobs, descriptions, assembly isolation) | ✅ working |
| Commands in / state + logs out over the control channel | ✅ working |
| Graceful stop, and crash-restart supervised by `AgentHost` | ✅ working |
| Choosing containers **from the portal / a policy** | ✅ working |
| The same digest running containerized on one agent and as a process on another | ✅ working |
| Port publishing for the *application's own* ports, static or engine-allocated | ✅ working |
| Port-collision detection between applications on one agent | ✅ working — wizard, policy screen and Applications tab |
| Per-policy image override and environment variables | ✅ working |
| Resource limits (CPU/memory) | ❌ not implemented — sketched in Container-Story.md §4.2 only |
| net472 in a container | ❌ rejected at policy creation — needs Windows containers (C6) |
| External ingress / a proxy-facing endpoint feed | ✅ working — verified against a real Traefik |

So: containers are a normal deployment choice now. See §1a for how to turn one on.

## 1a. Actually running something in a container

> **Every `curl` in this guide assumes the loopback developer setup**, where the control plane runs
> with `Authentication:Mode=Off` - which is allowed only because it is on loopback. Against anything
> else, add `-H "Authorization: Bearer <key>"` with an Operator key for the writes and a Viewer key
> for the reads (`Enlist.ControlPlane.exe create-api-key --name dev --role Operator`). Without it
> every one of them answers 401.

Two steps.

**1. Start the agent with a container image.** An agent only gains container support if you give it one — without this flag it has no container backend, and a policy asking for one is reported as a configuration problem rather than failing obscurely:

```bash
dotnet run --project src/Enlist.Agent -- \
  --control-plane http://localhost:5293 --agent DEV-AGENT-02 \
  --runner-bin src/Enlist.Runner/bin/Debug/net10.0 \
  --data C:/enlist-demo/agent2 \
  --container-image enlist/runner:dev
```

**2. Set the isolation mode on a policy rule.** In the portal, the "Runs as" dropdown in the new-rule wizard and the edit dialog. Or over the API:

```bash
curl -X POST http://localhost:5293/api/application-policies \
  -H "Content-Type: application/json" \
  -d '{"applicationName":"OrderProcessor","path":null,"desiredState":"Running",
       "cronOverrides":{},"tagSelector":{"dev":"test10"},
       "packageDigest":"<digest>","isolation":{"mode":"container"}}'
```

That is the whole change. **The package is not rebuilt, re-uploaded, or altered in any way** — the identical digest can back one process rule and one container rule at the same time, which is the point of the design (Container-Story.md §2.1). A rule with no isolation block means process, exactly as every rule did before the field existed.

Confirm it took:

```bash
docker ps --filter "name=enlist-" --format '{{.Names}}  {{.Status}}  {{.Image}}'
# enlist-OrderProcessor-bf945374  Up 13 seconds  enlist/runner:dev
```

The agent status report also carries `IsolationMode` ("process"/"container") and `RuntimeId` (a pid, or the container id) per application — the container id there is what you paste into `docker logs`.

**If a rule asks for a container and nothing happens**, check the agent log: an agent started without `--container-image` marks the application `Failed` with a line naming the mode it was asked for and the modes it actually supports. It does not retry — a missing engine will not appear on the next attempt.

---

## 1b. Publishing an application's ports

Add a `ports` array to the rule's isolation block. `hostPort` is optional:

```bash
curl -X PUT http://localhost:5293/api/application-policies/<ruleId> \
  -H "Content-Type: application/json" \
  -d '{"path":null,"desiredState":"Running","cronOverrides":{},
       "packageDigest":"<digest>",
       "isolation":{"mode":"container",
                    "ports":[{"containerPort":8080,"hostPort":null,"protocol":"tcp","name":"http"}]}}'
```

| `hostPort` | Behaviour | Use when |
|---|---|---|
| `null` | The engine allocates a free port | Default. Required for a tag-selector rule landing on many agents — one hard-coded port cannot serve them all. |
| a number | Published on exactly that port | Something outside enList already points at it. |

`name` is optional but worth setting: it is reported back and rendered, so an endpoint reads as `http` rather than a bare number. Ports are validated on save — range 1-65535, protocol tcp or udp — so a typo is rejected where you typed it rather than by `docker run`, whose complaint names neither the rule nor the application.

The isolation block carries three more fields, all honoured:

```json
"isolation": {
  "mode": "container",
  "image": "enlist/runner:dev",         // pin THIS application to one runner release (":dev" and ":wslc" are what this repo builds; a version tag is an open release decision)
  "networks": ["shared-net"],           // named networks to attach to
  "env": {"ASPNETCORE_ENVIRONMENT": "Staging"},   // non-secret only
  "ports": [{"containerPort": 8080}]
}
```

`image` overrides the agent's `--container-image` for this rule alone, which is the point: migrating one application to a new runner release should not require reconfiguring the agent. **`env` is for non-secret values only** — a policy row is rendered in plain text in the portal; secrets belong in a store the application reads at startup.

The agent restarts the application to apply this — a running container cannot adopt a new port mapping. Then:

```bash
docker ps --filter "label=enlist.agent=DEV-AGENT-02" --format '{{.Names}}	{{.Ports}}'
# enlist-OrderProcessor-023bd654   127.0.0.1:32851->5000/tcp, 0.0.0.0:65404->8080/tcp
```

Note the two mappings, and that they bind differently. The `5000` one is enList's own control channel, deliberately on `127.0.0.1` only. The `8080` one is the application's, deliberately on all interfaces — being reachable is the point of declaring it. **Declaring a port publishes it beyond the host.**

The resolved endpoint appears in the portal's Applications tab under **Endpoint**, and in the status report:

```json
"endpoints":[{"name":"http","protocol":"tcp","containerPort":8080,"hostPort":65404,"hostAddress":"KENSHO"}]
```

**Port collisions are caught twice, before anything breaks.** The new-rule wizard warns as you type, naming the agent, the port and the application that already holds it, and asks for confirmation before saving. Independently, policy resolution flags both sides as `Failed` — so a collision created by any other route (the API, a tag change that makes two rules newly overlap) is still caught. The engine's own "port is already allocated" names neither the application nor the rule, which is why neither check defers to it. Dynamic ports never collide; the same static port on two *different* agents is fine.

---

## 1c. Feeding a reverse proxy

enList publishes WHERE applications are listening. It does not route, terminate TLS, or own hostnames — see Container-Story.md §8.4 for why that boundary is deliberate.

```bash
curl -s http://localhost:5293/api/endpoints
# [{"applicationName":"OrderProcessor","agentName":"DEV-AGENT-02","name":"http","protocol":"tcp",
#   "containerPort":8080,"hostPort":50605,"hostAddress":"KENSHO","stale":false,
#   "reportedAtUtc":"2026-09-08T12:52:10Z"}]
```

Add `?application=OrderProcessor` to narrow it. **`stale: true`** means that agent has not checked in for over 5 minutes: the value is last-known, not confirmed. It is reported rather than hidden because "the agent went quiet" and "the application stopped" are different problems.

### Traefik

`/api/endpoints/traefik` renders the same data as Traefik dynamic configuration, with stale entries excluded:

```bash
curl -s http://localhost:5293/api/endpoints/traefik
# {"http":{"services":{"orderprocessor":{"loadBalancer":{"servers":[{"url":"http://KENSHO:50605"}]}}}}}
```

Point a Traefik at it. This exact command was run to verify the feature:

```bash
docker run -d --name enlist-traefik-test -p 127.0.0.1:8090:8080 traefik:v3.1 \
  --api.insecure=true \
  --providers.http.endpoint=http://host.docker.internal:5293/api/endpoints/traefik \
  --providers.http.pollInterval=5s
```

(`host.docker.internal` is how a container reaches the host. Start the control plane on `--urls https://0.0.0.0:5293` with a certificate, so it is reachable from outside loopback. **Plain `http://0.0.0.0:5293` will not start:** authentication defaults to `Required`, which refuses plain HTTP off loopback, and `Off` is honoured only when every listener IS loopback. The Traefik provider then needs a Viewer key on `GET /api/endpoints`.)

Confirm Traefik accepted it:

```bash
curl -s http://127.0.0.1:8090/api/http/services
# ...{"loadBalancer":{"servers":[{"url":"http://KENSHO:50605"}]},
#     "status":"enabled","name":"orderprocessor","provider":"http"}
```

`status: enabled` and `provider: http` mean Traefik polled enList, parsed the config and registered the service.

**enList emits `services`, never `routers`** — and that is the boundary, not an omission. A router is a hostname, a certificate and a path rule: how *your* organisation wants traffic to arrive. Declare those once in your own Traefik config and point each at a service named here:

```yaml
# your own traefik config, not generated by enList
http:
  routers:
    orders:
      rule: "Host(`orders.example.com`)"
      service: orderprocessor   # the name enList publishes
```

enList then keeps the server list under that service current on its own. Verified: killing the application container made the agent restart it on a new ephemeral port, the feed moved `50605 → 58350`, and Traefik followed within one poll — with nothing else touched.

---

## 1d. What if my application is .NET Framework?

It cannot run in a container, and enList now says so when you try:

```
HTTP 400
'net472' cannot run in a container: the enList runner image is Linux .NET 10, so the application's
assemblies would fail to load and it would report Running with nothing in it. Windows containers for
net472 are not supported yet — run this application as a process instead.
```

Run it as a process, on an agent started with `--legacy-runner-bin` pointing at the net472 runner build:

```bash
dotnet build src/Enlist.Runner.Legacy/Enlist.Runner.Legacy.csproj

dotnet run --project src/Enlist.Agent -- \
  --control-plane http://localhost:5293 --agent DEV-AGENT-01 \
  --runner-bin src/Enlist.Runner/bin/Debug/net10.0 \
  --legacy-runner-bin src/Enlist.Runner.Legacy/bin/Debug/net472 \
  --data C:/enlist-demo/agent1
```

**A policy inherits its flavor from its package.** Upload detects net472 (no `.deps.json`), and any rule pointing at that package is stored as net472 — you do not set it, and a request that contradicts the package is rejected. An agent with no `--legacy-runner-bin` reports such an application `Failed` with a line naming the flavor it needs and the flavors it has.

Windows containers would make net472-in-a-container possible; that is C6 at the earliest, and the image would be far larger.

---

## 2. Prerequisites

1. **Docker Desktop** (running, in **Linux container** mode) **or WSL 2.9.11+ with `wslc`** - see §2a, which is a full alternative, not a footnote: same seam, same image, same lifecycle, its own test class, and what the demo actually uses. Pick one; the rest of this section shows Docker.
   Verify — this must print `linux`:
   ```bash
   docker info --format '{{.OSType}}'
   ```
   If it errors with `failed to connect to the docker API at npipe:////./pipe/dockerDesktopLinuxEngine`, the daemon is not up. Start Docker Desktop and wait ~25s. It is normal for `docker` to be on `PATH` while the daemon is down — the CLI installs separately from the engine.

2. **.NET 10 SDK** on the host (already required to build enList at all).

3. **Git Bash users:** prefix any `docker` command containing a path with `MSYS_NO_PATHCONV=1`, or MSYS rewrites `/app` into a Windows path and the mount silently lands somewhere wrong:
   ```bash
   export MSYS_NO_PATHCONV=1
   ```
   PowerShell needs none of this.

---

## 2a. Choosing the engine: Docker Desktop or WSL containers

enList drives the engine through the `docker` CLI by default. Windows 11 with WSL 2.9.11 or later also ships `wslc` — the Windows Subsystem for Linux container CLI — and the agent can use it instead of Docker Desktop:

```bash
enlist-agent … --container-image enlist/runner:wslc --container-engine wslc
```

Same seam (`IContainerEngine`), same image built from the same Dockerfile (§3), same lifecycle; the Agents tab's capabilities chip says which engine an agent is on. Verified against wslc 2.9.11.0, and pinned by `WslContainerEngineTests` (§9). Five differences — four absorbed by `WslContainerEngine`, the last by the backend, for every engine:

- **A published port binds to 127.0.0.1 by default** — the opposite of Docker. Right for the unauthenticated control channel; wrong for an application port, so the agent publishes application ports with an explicit `0.0.0.0:`.
- **Host port 0 is rejected**, so a *dynamic* application port has no "let the engine pick a LAN port" spelling. The agent picks a free port itself and passes it fixed — a small race with anything else grabbing ports at that instant; a lost race fails the start loudly and the ordinary retry tries again.
- **No `port` verb** — host ports are read from `wslc inspect`.
- **No `wait` verb** — exit is polled once a second, so a crash takes up to a second longer to notice than under Docker.
- **The port proxy accepts before the runner listens** — a connection to the published control port succeeds the moment the container exists, and is closed a beat later if nothing inside is listening yet. That looks exactly like a runner dying during discovery, so the backend does not hand a connection over until the far end has spoken or stayed open for a second; one that closes first is dialled again. Verified with a container that never listens: every connect accepted, every one closed.

`wslc`'s image store is separate from Docker Desktop's: an image built with `docker build` is not visible to `wslc` and vice versa — build it with `wslc build` (§3) or `wslc load` a saved tar. `wslc` is installed at `C:\Program Files\WSL\wslc.exe` and is not always on PATH; the agent looks there when it is not.

---

## 3. Build the runner image

From the **repository root** — the Dockerfile lives under `src/Enlist.Runner/` but its build context is the root:

```bash
docker build -f src/Enlist.Runner/Dockerfile -t enlist/runner:dev .
```

Or, for the WSL container engine (§2a) — its own image store, its own tag:

```bash
wslc build -f src/Enlist.Runner/Dockerfile -t enlist/runner:wslc .
```

Takes ~30s cold, ~2s warm. Verify:

```bash
docker image inspect enlist/runner:dev --format '{{.Id}}'
```

**Rebuild the image whenever you change anything under `src/Enlist.Runner/`.** The image carries its own compiled copy of the runner; a `dotnet build` on the host does *not* update it. This is the single most common way to spend an hour debugging a fix that is not actually running — if a change to the runner seems to have no effect inside a container, rebuild the image first.

The image is deliberately generic: **one image for all applications**, containing only `enlist-runner`. The application is never baked in — it arrives at run time as a read-only bind mount. That is what makes "same package, containerized or not, no rebuild" true.

---

## 4. Run a container by hand

The exact command the agent issues, written out:

```bash
docker run -d --name enlist-sampleservice-a1b2c3d4 \
  --label enlist.agent=DEV-AGENT-01 --label enlist.application=SampleService \
  -p 127.0.0.1:0:5000 -v "C:/Users/you/src/enList_v3/deploy/SampleService:/app:ro" \
  enlist/runner:dev --listen 5000 --app /app
```

Piece by piece:

| Fragment | Why |
|---|---|
| `-d` | Detached. The runner has no console UI; everything comes over the control channel. |
| `-p 127.0.0.1:0:5000` | Publish the container's port 5000 to an **ephemeral host port, bound to loopback only**. The `127.0.0.1:` prefix is a security control, not cosmetics — see §6. The `0` lets the engine pick a free port so concurrent runners cannot collide. |
| `-v <hostPath>:/app:ro` | The extracted package directory, mounted **read-only**. Host path must be absolute and Windows-style (`C:/...` or `C:\...`). |
| `--listen 5000` | The runner binds this port **inside** the container and waits for the agent to dial in. |
| `--app /app` | Where to discover plugins — always `/app`, the mount point. |
| `--name enlist-<app>-<8 hex>` | The naming scheme §8.1 describes. |
| `--label enlist.agent=` / `enlist.application=` | **The ownership record, and load-bearing.** Orphan reaping (§8.2) finds containers by these labels, so a container created by hand WITHOUT them is one no agent will ever clean up - exactly the trap §8.2 warns about. They were missing from this command until 2026-09-13, which made "the exact command" a way to create that trap. |

Then find the host port the engine chose:

```bash
docker port <containerId> 5000/tcp
# 127.0.0.1:32768
```

The container **exits by itself after 10 seconds** if nothing connects. That is intentional (a runner nobody is driving should not linger), but it means you must connect promptly when poking by hand. If your container keeps vanishing before you can talk to it, this is why.

---

## 5. Talk to a running container

The control channel is newline-delimited JSON — one JSON object per line, in both directions. Connecting to it and reading one line gets you the `ready` message.

Save this as `probe.cs` anywhere and run it with `dotnet run probe.cs <hostPort>`:

```csharp
using System.Net.Sockets;

var port = int.Parse(args[0]);

using var client = new TcpClient();
for (var attempt = 1; attempt <= 40; attempt++)
{
    try { await client.ConnectAsync("127.0.0.1", port); break; }
    catch (SocketException) when (attempt < 40) { await Task.Delay(250); }
}

Console.WriteLine($"connected to 127.0.0.1:{port}");

using var stream = client.GetStream();
using var reader = new StreamReader(stream);
var line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
Console.WriteLine(line is null ? "NO LINE (peer closed)" : line);
```

The retry loop is **required, not defensive**: `docker run` returns as soon as the container process is created, which is well before the .NET runtime has started and bound the port. The first several connects legitimately get refused. `ContainerRunnerBackend.ConnectAsync` does exactly the same thing.

Real output from the SampleService package:

```json
{"kind":"ready","services":[{"name":"Sample Service","description":"Logs a heartbeat until stopped.","typeName":"Enlist.Sample.Service.SampleService"}],"jobs":[{"name":"Sample Job","description":"Runs once and reports its settings.","typeName":"Enlist.Sample.Service.SampleJob","declaredCron":"0 0 2 * * ?"}],"warnings":[],"isolation":{"depsFilesFound":1,"resolverCount":1,"privateResolutionCount":0,"orphanedDeps":[]},"applicationDescription":"A minimal example application — one heartbeat service and one nightly job.","protocolVersion":1}
```

What to check in that line:

- **`services` / `jobs` non-empty** — discovery found the plugin. Empty usually means the mount is wrong (§8).
- **`isolation.depsFilesFound > 0`** — assembly isolation engaged. `0` is the real failure signal; `resolverCount: 0` on its own is healthy for a plugin with no private dependencies.
- **`protocolVersion: 1`** — the runner speaks the contract this agent expects. See §7.

To drive it further, write a line back — e.g. `{"kind":"startService","service":"Sample Service"}` — and keep reading.

---

## 6. Why the runner *listens* instead of dialling out

Outside a container, the runner **dials out** to a named pipe (`--pipe`) the agent created. In a container it does the opposite: it **listens** on TCP (`--listen`) and the agent dials in. Two independent reasons, both discovered the hard way:

**1. A Unix domain socket cannot cross a Windows host → Linux container boundary.** This was tested directly rather than assumed. A UDS created by .NET on the Windows filesystem, bind-mounted into a Linux container, arrives as an **ordinary empty regular file**:

```
-rwxrwxrwx  1 root root 0 Sep  7 21:06 probe.sock
RESULT: presents as a REGULAR FILE — not connectable as a socket
```

So the `--socket` transport built in C1, though genuinely working host-to-host, is unusable for containers on a Windows host. Reproduce it yourself with the recipe in §8 if you ever need to re-check on a different platform.

**2. The direction that *would* work is insecure.** The agent could listen and the container could dial `host.docker.internal` (which does resolve — `192.168.65.254`). But the agent would then have to bind an interface reachable from outside the host, and **this protocol has no authentication of any kind**: anything that connects can start services and run jobs. Listening *inside* the container instead lets the engine publish the port to `127.0.0.1` only, so the channel is unreachable from the LAN.

> **Security note, carried forward.** The channel is still unauthenticated; loopback-only publishing is what confines it. Anything that changes that binding — attaching containers to a shared network, publishing on `0.0.0.0`, a remote engine — reopens the exposure. This needs a real answer before containers run anywhere but a developer's machine.

---

## 7. Protocol version

`ReadyMessage.protocolVersion` (currently `2`) is checked by the agent before it acts on anything else in the message. A mismatch fails the application immediately with a clear reason and **is not retried** — a differently-versioned binary will still be differently-versioned next time.

This matters far more for containers than for processes: outside a container the runner is staged from a directory deployed alongside the agent, so the two literally cannot disagree. A separately-built **image** can. If you rebuild the image from a different branch than the agent, this check is what turns an obscure downstream failure into one clear log line.

An old runner that predates versioning reports `0` and is diagnosed distinctly ("did not report a protocol version") from a genuine mismatch.

**Version 2 went in on 2026-09-13, and any image built before that date will now be refused.** Version 1 was the pre-versioning contract. It stayed at 1 through the change that turned `JobResultMessage.success` (a bool) into `outcome` (an enum), on the reasoning that every peer is rebuilt together - which stopped being true the moment the runner shipped as an image you build separately. An image from before the change reports version 1 and its `"success": false` deserializes as `Outcome = Succeeded`, so a failed job is reported as a clean one and nothing says otherwise. The refusal replaces that silence. Rebuild the image (section 3) and it goes away.

---

## 8. Debugging

**Look at the container's own output.** The runner writes startup failures to stderr before the channel exists:

```bash
docker logs <containerId>
```

**`docker` hangs and nothing progresses.** Every engine command except `wait` is bounded at 60 s (`DockerContainerEngine`'s `commandTimeout`); on expiry the CLI is killed and the start fails with a `TimeoutException` naming the command, which the agent retries like any other start failure. Restart the daemon and let the retry find it.

**Container exits immediately, logs empty.** Almost always the 10-second connect timeout in §4 — nothing dialled in. Not a bug.

**`ready` arrives with empty `services` and `jobs`.** The mount is wrong. Check what the container can actually see:

```bash
docker run --rm -v "C:/your/package/path:/app:ro" --entrypoint ls enlist/runner:dev -la /app
```

A healthy mount lists the plugin assemblies and their `.deps.json`:

```
-rwxrwxrwx 1 root root   457 Enlist.Sample.Service.deps.json
-rwxrwxrwx 1 root root  8704 Enlist.Sample.Service.dll
```

If `/app` is empty or missing, the host path is wrong — or (Git Bash) you forgot `MSYS_NO_PATHCONV=1` and MSYS rewrote it.

**Verify the port mapping and container state:**

```bash
docker ps -a --filter "name=enlist-" --format '{{.Names}}\t{{.Status}}\t{{.Ports}}'
docker inspect <containerId> --format '{{.State.Status}} exit={{.State.ExitCode}}'
```

Agent-started containers are named `enlist-<applicationName>-<8 hex>`, specifically so `docker ps` during an incident tells you which application is which without correlating ids.

### 8.1 Which agent owns a container

Every container the agent creates carries two labels:

| Label | Meaning |
|---|---|
| `enlist.agent` | The agent that created it — **this is the ownership record** |
| `enlist.application` | The enList application running inside it |

> **Shell warning — the format strings below are quoting-sensitive.** A Go template argument like
> `{{.Label "enlist.agent"}}` contains double quotes *inside* a quoted argument, and **PowerShell strips
> them** before `docker` ever sees them. The template then reads as `{{.Label enlist.agent}}` and you get:
>
> ```
> failed to parse template: template: :1: function "enlist" not defined
> ```
>
> That error means the quoting was eaten, not that anything is wrong with the container. **In PowerShell,
> escape the inner quotes with a backslash: `\"enlist.agent\"`.** Bash and cmd take them plain. Both forms
> are given below; commands with no inner quotes (every `--filter`, and `{{json .Config.Labels}}`) work
> unchanged in either shell.

**Show owner and application as columns.** The most useful of these day to day, because a real machine's `docker ps` is mostly containers that have nothing to do with enList:

```bash
# bash / cmd
docker ps -a --format 'table {{.Names}}\t{{.Label "enlist.agent"}}\t{{.Label "enlist.application"}}'
```

```powershell
# PowerShell — note the backslashes
docker ps -a --format 'table {{.Names}}\t{{.Label \"enlist.agent\"}}\t{{.Label \"enlist.application\"}}'
```

```
NAMES                            agent          application
enlist-LegacySample-0a75606d     DEV-AGENT-02   LegacySample
enlist-OrderProcessor-a5f297f8   DEV-AGENT-02   OrderProcessor
redisinsight
mudtray-redis
```

**Narrow to one agent:**

```bash
docker ps --filter "label=enlist.agent=DEV-AGENT-02"
```

**Everything enList owns, whichever agent owns it** — the sweep to reach for when you want to see, or clear, all of it:

```bash
docker ps -a --filter "label=enlist.agent"
```

**All labels on one container** — no inner quotes, so this one is identical in every shell:

```bash
docker inspect enlist-OrderProcessor-a5f297f8 --format "{{json .Config.Labels}}"
# {"enlist.agent":"DEV-AGENT-02","enlist.application":"OrderProcessor","org.opencontainers.image.version":"24.04"}
```

**Just the owner of one container:**

```bash
# bash / cmd
docker inspect <name> --format '{{index .Config.Labels "enlist.agent"}}'
```

```powershell
# PowerShell — single quotes outside, backslash-escaped quotes inside, same rule as above
docker inspect <name> --format '{{index .Config.Labels \"enlist.agent\"}}'
```

**In Docker Desktop:** the container list has no label column, but click a container → **Inspect** → `Config.Labels`:

```json
"Labels": {
    "enlist.agent": "DEV-AGENT-02",
    "enlist.application": "LegacySample",
    "org.opencontainers.image.version": "24.04"
}
```

The Desktop search box matches on name, so typing `enlist-` narrows to enList's containers — it just cannot filter by label the way the CLI can.

Two things worth knowing about these labels:

- **`enlist.agent` is the reap key** (§8.2). Orphan cleanup deletes by this label and nothing else. If you hand-create a container and label it `enlist.agent=DEV-AGENT-02`, that agent will delete it on its next startup.
- **`enlist.application` duplicates what is already in the container name, deliberately.** The name is for a human scanning `docker ps`; the label is for `--filter` and for tooling, and does not break if the naming scheme ever changes.

### 8.2 Orphaned containers, and why they happen

A container **outlives the agent that started it**. This is the one place containers are *weaker* than the process backend, not stronger: a locally-spawned runner is enrolled in a Windows Job Object, so an abnormally-dead agent takes its runners down with it. Nothing equivalent links a container to the agent's lifetime.

So a hard-killed agent — Ctrl+C on the wrong window, a crash, `Stop-Process` — leaves its containers behind. The runner inside each one does notice: its control channel ends (end-of-stream, or a reset relayed by the engine's port proxy), and it stops its services and exits exactly as it does when a pipe closes — so what is left is a *stopped* container, never a running one. But it is left. Graceful shutdown removes them; an unclean exit does not.

**The agent reaps its own on startup.** Before it starts anything, it removes every container labelled with its own agent name — at that moment it owns nothing, so anything still bearing its label is by definition a leftover. It says so in the agent log:

```
Removed 2 orphaned container(s) left by a previous run of this agent.
```

Seeing that line means the previous run did not exit cleanly. The containers are already gone; it is a signal, not a task.

**The reap is scoped by label, never by the `enlist-` name prefix**, and that distinction is load-bearing: several agents commonly share one engine (two do on this project's dev box), and a prefix-based sweep by one agent would destroy another agent's *live* containers.

The consequence worth remembering: **containers created before labelling existed have no `enlist.agent` label, so no agent will ever reap them.** Clear those once, by hand:

```bash
docker rm -f $(docker ps -aq --filter "name=enlist-")
```

**Reproduce the Unix-socket finding from §6** (only needed if re-checking on another platform):

```bash
# 1. Create a UDS on the host. Path must be SHORT — the limit is 108 bytes,
#    and a normal temp path plus a GUID can exceed it.
mkdir -p /c/enltest
# ...bind a Socket(AddressFamily.Unix, ...) at C:\enltest\probe.sock, keep it listening...

# 2. See what a Linux container makes of it.
MSYS_NO_PATHCONV=1 docker run --rm -v "C:/enltest:/mnt/probe" alpine:3 \
  sh -c '[ -S /mnt/probe/probe.sock ] && echo SOCKET || echo "NOT a socket"'
```

Note **Windows PowerShell 5.1 cannot do step 1** — it runs on .NET Framework, which has no `UnixDomainSocketEndPoint`. Use `dotnet run file.cs` or PowerShell 7+.

**Cleanup** after manual poking:

```bash
docker rm -f $(docker ps -aq --filter "name=enlist-")
```

The tests clean up after themselves; verify with:

```bash
docker ps -a --filter "name=enlist-" -q | wc -l   # expect 0
```

---

## 9. Running the tests

The container tests live in `tests/Enlist.Agent.Tests/`: the four Docker classes below, plus `WslContainerEngineTests`, which proves the same lifecycle on `wslc` and skips itself when `wslc` or `enlist/runner:wslc` is missing, and `ContainerControlChannelTests`, which needs no engine at all: it plays the port proxy of §2a itself against the dial-in in `ContainerRunnerBackend`.

| File | Proves |
|---|---|
| `ContainerRunnerBackendTests.cs` | A package runs containerized; discovery, identity, commands, graceful stop |
| `ContainerCrashRestartTests.cs` | `AgentHost` supervises a container and restarts it after `docker kill` |
| `ContainerOrphanReapTests.cs` | A container abandoned by a killed agent is reaped at next startup — AND that one agent's reap leaves another agent's live containers alone |
| `ContainerPortPublishingTests.cs` | Static and engine-allocated ports are published and reported back as resolved endpoints |
| `PortCollisionTests.cs` (ControlPlane) | Two applications claiming one static host port both fail — and the three cases that must NOT be conflicts |
| `EndpointFeedTests.cs` (ControlPlane) | The endpoint projection, the Traefik shape, and that an unreadable snapshot degrades instead of breaking the feed |
| `FlavorIsolationTests.cs` (ControlPlane) | A policy inherits its package's flavor; net472 + container is rejected; a Path-based rule keeps its flavor |
| `IsolationFieldTests.cs` (ControlPlane) | Port range and protocol validation; image/env/networks round-trip |
| `LoadFailureWarningTests.cs` (Runner) | An unloadable assembly is named in the warnings, and a clean application emits none |

Build the image first, then:

```bash
dotnet test tests/Enlist.Agent.Tests/Enlist.Agent.Tests.csproj --filter "FullyQualifiedName~Container"
```

Or the whole suite (273 tests):

```bash
dotnet test enList_v3.slnx
```

**These tests skip themselves — they do not fail — when Docker or the image is unavailable.** That is deliberate: the suite must stay runnable on a machine with no container engine, and a red test there would say "enList is broken" when it means "Docker isn't installed". If you expect them to run and they report *skipped*, you have not built the image (§3).

A note on flakiness: the full suite runs four projects in parallel, and the container tests add real load. A single control-plane test has been seen to fail once under that contention and pass on re-run. If you get exactly one unexplained failure, re-run before investigating.

`ContainerCrashRestartTests` is also the standing check on the architecture's central invariant: `AgentHost` is handed a `ContainerRunnerBackend` and is otherwise **completely unmodified** — no container-aware configuration, no special casing. If someone ever adds `if (isContainer)` to `AgentHost`, the design has leaked; that test existing in this shape is the guard.

---

## 10. How the pieces fit

```
AgentHost ──holds──> IRunnerInstance        (never knows which kind)
    │
    └──calls──> IRunnerBackend.StartAsync
                     ├── ProcessRunnerBackend ──> ProcessRunnerInstance   (named pipe / unix socket)
                     └── ContainerRunnerBackend ──> ContainerRunnerInstance
                                  │
                                  └──> IContainerEngine ──> DockerContainerEngine  (`docker` CLI)
```

| File | Role |
|---|---|
| `src/Enlist.Runner/Dockerfile` | The runner image |
| `src/Enlist.Runner/Program.cs` | `--pipe` / `--socket` / `--listen` transport selection |
| `src/Enlist.Agent/Supervision/IContainerEngine.cs` | Engine seam + `ContainerSpec` |
| `src/Enlist.Agent/Supervision/DockerContainerEngine.cs` | Drives the `docker` CLI |
| `src/Enlist.Agent/Supervision/WslContainerEngine.cs` | Drives the `wslc` CLI (WSL containers) — see §2a |
| `src/Enlist.Agent/Supervision/CliContainerEngineBase.cs` | What both engines share: one bounded, killed-on-expiry command runner |
| `src/Enlist.Agent/Supervision/ContainerRunnerBackend.cs` | Starts a container, dials its control port |
| `src/Enlist.Agent/Supervision/ContainerRunnerInstance.cs` | One live container, as `AgentHost` sees it |

`DockerContainerEngine` shells out to the `docker` CLI rather than using the HTTP API. Every operation is a single short-lived command with one line of output, and the CLI is what *you* reach for when diagnosing the same containers by hand — so what the agent does and what you do stay directly comparable.

---

## 11. Known gaps

- ~~The Applications tab does not surface isolation per running instance.~~ Fixed on BOTH running views: the Applications tab chips each running row beside the agent name, and the Agents tab chips the application beside its state. Both tooltip `docker logs <short id>` from `RuntimeId`. Process rows deliberately carry no chip — process is the default and the overwhelming majority, so a chip there would be noise.
- ~~Staging is wasted work for containers.~~ Fixed in C3: staging moved behind `IRunnerBackend`, so a containerized application no longer copies a runner nobody reads.
- **`Pid` is `0` for containers.** Deliberate — a container's "host pid" on Docker Desktop lives inside a Linux VM and is meaningless to anything on the Windows host. Use `RuntimeId` (the container id) instead; it is what `docker logs` accepts. The status report carries `IsolationMode`, `RuntimeId` and `Endpoints` alongside it (C3), and the portal renders all three - the container chip, the `docker logs` tooltip and the Endpoint column. Only the first sentence of this bullet was ever the lasting part; the rest described a gap that closed and is removed.
- **The control channel is unauthenticated.** See the security note in §6.
- **Linux containers only.** The image is `mcr.microsoft.com/dotnet/runtime:10.0`. A net472 application cannot run in it; that needs Windows containers, which is C6 at the earliest.
- **The mismatch path in §7 is not integration-tested.** `RunnerProtocol.DescribeMismatch` is unit-tested and the happy path is proven end-to-end, but forcing a real runner to report a wrong version would mean adding a test-only override to production code.
