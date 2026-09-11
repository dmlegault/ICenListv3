# enList v3 — Documentation

enList runs small programs (services and scheduled jobs) across many Windows machines, controlled
from one web portal. You upload a package, write a rule saying *where* it should run, and agents on
each machine make it so.

There are 20 documents here. **You do not need most of them to be productive.** This page is the
map: a short path in, then tracks depending on what you actually came to do.

---

## Start here

Four documents, roughly two hours, in this order. This is the fastest honest route from "never seen
this before" to "I can find my way around the code".

| # | Read | Why | Rough time |
|---|---|---|---|
| 1 | [**One-Page-Overview.md**](01-start-here/One-Page-Overview.md) | What enList is, in plain language, with no jargon and no architecture. Start here even if you are very senior — it names the five components and what each is *for*, which every other document assumes you know. | 5 min |
| 2 | [**Developer-Setup-Guide.md**](01-start-here/Developer-Setup-Guide.md) | Clone to a running system: build, start the control plane and portal, install an agent, deploy a sample application and watch it run. Do this before reading any theory — the rest lands far better once you have seen it work. | 30–45 min, hands on |
| 3 | [**enList-v3-Design.md** §1–§3](03-architecture/enList-v3-Design.md) | The original design document, and the "why" behind almost every non-obvious choice. §1 is the constraint that decides the entire architecture; read at least that far. | 20 min |
| 4 | [**SAD.md**](03-architecture/SAD.md) | System Architecture. The five components, how they talk, and — in §9 — where the shipped system deliberately diverged from the design doc above. | 25 min |

After those four you will understand the system's shape, have it running locally, and know where
the code lives. **Stop there and pick a track below** rather than reading straight through.

---

## Then pick a track

| If you want to… | Read, in order |
|---|---|
| **Write an application that runs on enList** (the most common case — you are a consumer of the platform, not a maintainer) | [Application-Developer-Guide.md](02-building-applications/Application-Developer-Guide.md). Note §8 especially: the agent behaviours the dev host deliberately does *not* reproduce. Then [Container-Developer-Guide.md](02-building-applications/Container-Developer-Guide.md) if your application will run containerized. |
| **Work on the platform itself** | [HLD.md](03-architecture/HLD.md) for module decomposition and the key sequence diagrams, then [LLD.md](03-architecture/LLD.md) for the algorithms worth knowing exactly. Add [Database-Design.md](03-architecture/Database-Design.md), [API-Specification.md](03-architecture/API-Specification.md) and [UI-UX-Design-Spec.md](03-architecture/UI-UX-Design-Spec.md) as you touch those areas. |
| **Deploy or operate it** | [Deployment-IaC.md](05-operations/Deployment-IaC.md), then [Runbook.md](05-operations/Runbook.md). The proposed installer that replaces the manual steps is designed, page by page, in [Installer-UI-Design.md](05-operations/Installer-UI-Design.md) (a draft — nothing built yet). |
| **Understand containers** | [Container-Story.md](03-architecture/Container-Story.md) — the reasoning and the phased plan (C0–C5 shipped; only C6 remains a proposal). [Container-Developer-Guide.md](02-building-applications/Container-Developer-Guide.md) is the practical companion, and the one to read if you want to actually *run* any of it. |
| **Know what the system must do**, formally | [BRD.md](04-requirements/BRD.md) → [SRS.md](04-requirements/SRS.md) → [FRS.md](04-requirements/FRS.md). Useful for scoping and review; not the fastest way to learn how anything works. |
| **Change something and not break it** | [Test-Plan.md](05-operations/Test-Plan.md) — what the suite covers and, importantly, what it does not yet. |

**Two documents repay reading early even though nothing forces you to.**
[Runbook.md](05-operations/Runbook.md) §3 is a set of postmortems of real defects found during
development — symptom, root cause, fix, and how to recognise a recurrence. It is the fastest way to
learn this system's genuine traps. And the "does not reproduce" list in
[Application-Developer-Guide.md](02-building-applications/Application-Developer-Guide.md) §8 will
save you an afternoon of wondering why your cron job never fired locally.

---

## The folders

| Folder | What is in it |
|---|---|
| [`01-start-here/`](01-start-here/) | Orientation and getting it running on your machine. |
| [`02-building-applications/`](02-building-applications/) | For people writing *applications* that run on enList, rather than working on the platform. |
| [`03-architecture/`](03-architecture/) | How it is built and why: the original design, architecture, module and class-level design, schema, API contract, UI conventions, and the container design. |
| [`04-requirements/`](04-requirements/) | Business, system and functional requirements. |
| [`05-operations/`](05-operations/) | Deploying it, running it, and the test suite. |
| [`06-background/`](06-background/) | Prior art and historical artefacts. Not needed to work on enList. |

Related, outside this folder: [`demo/sql-server/`](../demo/sql-server/README.md) runs SQL Server in a WSL
container (`wslc`, no Docker) for demos, so demo data persists and is visible in SSMS. Day-to-day development and the
test suite both use LocalDB and need none of it.

---

## How to read this set critically

Most of these documents (BRD through Runbook, 13 of them) were **reverse-documented on 2026-09-03**
from the shipped implementation under `src/`, `samples/`, `contracts/` and `tests/`. They describe
what the code does, not what someone once intended it to do. Several were revised again on
2026-09-07/08 after audits found them describing a portal structure and CLI flags that no longer
existed — each says so in its own header.

Four documents are different in kind, and it matters:

- [**enList-v3-Design.md**](03-architecture/enList-v3-Design.md) is the **real original design
  document**, not reverse-documented. Every "design doc section N" reference in the source code
  points here. It is authoritative on *why*.
- [**Aware-Architecture-Notes.md**](06-background/Aware-Architecture-Notes.md) is 2009-era prior-art
  research the design doc cites throughout — a description of what an earlier system did, explicitly
  *not* an endorsement of porting it.
- [**Container-Story.md**](03-architecture/Container-Story.md) began as a proposal and is now mostly
  shipped. Phases C0–C5 are real code; only C6 remains proposed. Read its header for the current line.
- [**enList-v3-Review-2026-09-09.md**](06-background/enList-v3-Review-2026-09-09.md) is a point-in-time
  architecture and code review — 30 ranked findings with file and line, verified against the running
  demo fleet, and the order to fix them in. Line numbers there are as of that date and will drift; the
  finding IDs (C1, H1, …) are what later commits and Runbook entries refer to.

Where the shipped system deviates from the original design, that is called out rather than smoothed
over — chiefly in [SAD.md §9](03-architecture/SAD.md#9-design-vs-implementation) and
[Database-Design.md §7](03-architecture/Database-Design.md#7-design-doc-vs-implemented-schema).
[Deployment-IaC.md](05-operations/Deployment-IaC.md) is partly a proposal for work not yet done and
marks those sections explicitly. **When a document and the code disagree, the code is right and the
document has drifted** — please fix it, per below.

---

## Keeping this documentation current

These are not auto-generated and will drift. When you make a change that touches:

| Change | Update |
|---|---|
| A REST endpoint, SignalR message, or DTO shape | [API-Specification.md](03-architecture/API-Specification.md) |
| A database entity, migration, or index | [Database-Design.md](03-architecture/Database-Design.md) |
| A background loop, algorithm, or class responsibility | [LLD.md](03-architecture/LLD.md) |
| A new page, component, or visual convention in Enlist.Portal | [UI-UX-Design-Spec.md](03-architecture/UI-UX-Design-Spec.md) |
| A new or changed CLI flag, launch command, or setup step | [Developer-Setup-Guide.md](01-start-here/Developer-Setup-Guide.md) |
| A new test, or a newly discovered coverage gap | [Test-Plan.md](05-operations/Test-Plan.md) |
| A real incident worth remembering — a subtle bug, a footgun, a "this looked fine but wasn't" | [Runbook.md](05-operations/Runbook.md) §3, in the same postmortem format as the existing entries: symptom, root cause, fix, how to recognise it if it recurs |

If you move or rename a document, note that **source comments reference these paths** — around 40
of them across `src/`, `contracts/` and `tests/`, including one inside a `.csproj` build-error
message. Grep for `docs/` before and after.
