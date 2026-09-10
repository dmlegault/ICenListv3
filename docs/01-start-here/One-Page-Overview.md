# enList v3 — What Is It? (A One-Page Summary)

## The Big Idea

Imagine a company runs 50 computers in a server room, and each one needs to run several small programs all day, every day — things like "watch for new customer orders" or "email a report every night at 2 AM." Without help, someone would have to walk up to (or remote into) every single computer to check if a program is running, start it, stop it, or update it. That takes forever and doesn't scale.

**enList is a system that lets one person control all of this from a single website**, instead of touching every computer by hand.

## The Cast of Characters

enList is built from five pieces that work together, kind of like departments in a company:

| Piece | What It's Really Doing | Think of It As... |
|---|---|---|
| **Portal** | The website you click around in | The dashboard / control room |
| **Control Plane** | Remembers what *should* be running, and where | The manager's notebook |
| **Agent** | A helper program installed on every computer | A supervisor stationed on each floor |
| **Runner** | Actually starts and hosts one program | The worker doing the job |
| **enlist-deploy** | A tool developers use to publish a new version of a program | The delivery truck |

## How It Actually Works

1. A developer builds a small program — say, one that watches for new orders. They run `enlist-deploy`, which zips it up and uploads it.
2. On the Portal website, someone writes an **application policy rule**: "run this program on any server tagged `region=east`". Targeting is always by tag — and because every computer automatically carries a tag naming itself, "just run it on Server #7" is simply the narrowest possible tag rule rather than a separate feature.
3. Every computer runs an **Agent**, which constantly checks in with the Control Plane and asks, "what am I supposed to be running right now?"
4. When the Agent sees a new rule that applies to it, it downloads the program and hands it to a **Runner**, which actually starts it — either as an ordinary program on that computer, or inside a container, whichever the rule asks for. The same uploaded program can do both, with nothing rebuilt.
5. A program can contain two kinds of things:
   - **Services** — run continuously, like a heartbeat (e.g., "watch for new orders" runs forever, until told to stop).
   - **Jobs** — run on a schedule, like an alarm clock (e.g., "send a report every night at 2 AM").
6. From the Portal, a person can see exactly what's running where, watch live logs, and click a button to turn any of it on or off — instantly, without ever touching the actual computer.

## Why This Is Harder Than It Sounds

- **Computers come and go.** A server might restart, lose connection, or get re-tagged. The Agent keeps "phoning home" so the system always knows what's *really* happening — not just what it hopes is happening.
- **Not every computer is the same.** Some servers still run software written for a much older platform, over a decade old. enList can run a special "legacy" Runner on those machines, while newer machines run a modern one — all controlled from the very same dashboard.
- **Mistakes should be loud, not silent.** If someone tells enList to run a program on a computer that can't actually run it, enList says so clearly instead of quietly failing.

## In One Sentence

**enList is a remote control for computer programs — it lets a person decide what should run and where, then turns that decision into reality across dozens of computers, automatically.**
