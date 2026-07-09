🌐 [Leia em Português](README.pt-BR.md) | **English**

---

# NXProject Community

**Management visibility over Azure DevOps — without changing anything in the technical team's workflow.**

NXProject lets Tech Leads, Scrum Masters, Project Managers, and Business Stakeholders see the real project picture straight from Azure DevOps: schedule, dependencies, resource allocation, and Gantt chart — in a free Windows desktop application.

The development team keeps working in Azure DevOps exactly as before: code traceability, pull requests, pipelines, and delivery quality remain untouched. NXProject reads that data and turns the backlog into a planning view that managers and leads can actually use to make decisions.

---

## The problem NXProject solves

IT projects using Azure DevOps have an organized backlog, defined sprints, and updated work items — but **management has no integrated schedule view**. Simple questions go unanswered:

- When will this Feature be done, considering all its Stories?
- Which resource is overloaded next month?
- If this Story slips, what else gets impacted?
- Is the project going to deliver on time?

NXProject imports the Azure DevOps hierarchy and turns that data into a manageable schedule, with Gantt, dependencies, allocation, and delay alerts — **without requiring the technical team to change anything in their process**.

---

## Every role sees what they need, without friction

The development team keeps using Azure DevOps as the single source of truth: linked commits, code review, pipeline automation, and full traceability remain intact. NXProject is a **read-and-planning layer** on top of that data, aimed at those who need to answer questions about deadlines, capacity, and risk.

---

## The story behind NXProject

NXProject wasn't born as a product. It was born to solve a real problem.

At the time, my wife was pursuing a Master's degree in Education Management and needed to build a project schedule for a school ramp renovation. The need seemed simple: organize tasks, dependencies, and track the plan visually.

We looked for free tools, but the open-source options we found were outdated, and commercial alternatives required licenses I didn't have at that moment — I was between jobs.

So one weekend, I decided to build a simple alternative to turn tasks into a visual schedule and make it easier to track the project.

The initial goal was just to solve that one problem.

But as I built it, I realized the challenge was much bigger.

After more than 20 years working in technical leadership in data and software engineering, I kept seeing the same conflict in technology projects: technical tools worked great for development and data engineering teams, while management tools delivered schedules and reports — but often at the cost of parallel processes, rework, and lost traceability.

Technical teams needed to keep working in their day-to-day tools.

Managers needed to understand deadlines, capacity, dependencies, and risks.

Usually, someone had to give something up.

That's when the project stopped being just a schedule generator and evolved into NXProject.

Months later, as the idea matured and AI-assisted development tools advanced — after going deeper into environments like Codex and Claude Code — the product evolved quickly. What started as a simple prototype gained new capabilities in planning, visualization, and integration, allowing me to accelerate the vision that had existed from the beginning.

Later, when I integrated with Azure DevOps, I realized the same concept also helped real software and data engineering teams: teams kept working in their established flow — backlog, code, pipelines, automations, and traceability — while leaders and managers finally gained an integrated view of schedule, dependencies, capacity, and impact.

Today NXProject turns Azure DevOps data into a management view of planning and execution, allowing the technical and the managerial to work together — without friction, without parallel processes, and without giving up traceability.

---

## Download

**First time installing?** Download and run the Setup — it installs the .NET runtime, third-party libraries and a Desktop shortcut, then automatically fetches the latest NXProject version:

- [Download NXProject-Setup.zip](../../releases/latest/download/NXProject-Setup.zip)

**Already installed?** Just grab the small update package and extract it over your existing installation:

- [Download NXProject.Community-Release.zip (update)](../../releases/latest/download/NXProject.Community-Release.zip)
- [View release notes and source code downloads](../../releases/latest)

> `NXProject.Community-Release.zip` alone does **not** include the .NET runtime or third-party libraries (PdfSharp, WebView2, CommunityToolkit.Mvvm) — it only contains the app files (`.exe`/`.dll`) that change every release, and must be extracted **on top of** an existing installation (created by NXProject-Setup or a previous full release). Use NXProject-Setup.zip for a first-time install on a new machine.
>
> The binaries were built in an environment with McAfee antivirus. If you prefer to build from source, see the instructions below.

---

## If Windows blocks the .exe

Windows may refuse to run `NXProject.Community.exe` with a "Windows protected your PC" SmartScreen dialog, or simply do nothing when you double-click it. This happens because the binary is unsigned and was downloaded from the internet.

### Option 1 — Unblock via Properties (simplest, no admin required)

1. Right-click `NXProject.Community.exe` → **Properties**
2. At the bottom of the **General** tab, check **Unblock**
3. Click **OK** and double-click the `.exe` again

If the checkbox is not there, the file was already unblocked (or your system uses a stricter policy — see options below).

### Option 2 — Sign with a local developer certificate (recommended for organizations)

Run the script below **as Administrator** once. It creates a self-signed code-signing certificate, installs it as a trusted publisher on the machine, and signs all `.exe`/`.dll` files in the build output:

```powershell
# Run as Administrator in the project root
.\sign-nxproject.ps1
```

After that, run normally with `.\run-community.ps1` or double-click the `.exe`. **No parameter is needed** — the certificate is installed in the machine store and Windows picks it up automatically.

> The certificate is valid for 10 years and covers all future builds as long as you re-run `sign-nxproject.ps1` after each new release.

### Option 3 — WDAC supplemental policy (for corporate environments with strict execution policy)

If your organization enforces Windows Defender Application Control (WDAC) and neither option above works, run the WDAC script as Administrator to allow the NXProject folder:

```powershell
# Run as Administrator
.\allow-nxproject-wdac.ps1
```

This creates a supplemental WDAC policy that allows executables from the NXProject folder. A reboot may be required.

> This option is only needed in tightly locked corporate environments. Most users only need Option 1 or 2.

---

## Screenshots

![NXProject Community main screen](ScreenShot/Tela01.png)
![Hierarchy and Gantt view](ScreenShot/Tela02.png)
![Configuration and tracking](ScreenShot/Tela03.png)
![TFS / Azure DevOps import](ScreenShot/Tela04.png)
![Azure DevOps Backlog concept](ScreenShot/Tela05-Azure-DevOps-Backlog.svg)

---

## Who is NXProject for

| Role | What NXProject delivers |
|---|---|
| **Project Manager** | Schedule integrated with the backlog, delay alerts, dependency view |
| **Scrum Master / RTE** | Capacity per sprint, allocation conflicts, impact of date changes |
| **Tech Lead** | Features and Stories with predecessors and hour-based estimates |
| **PMO** | Multi-project consolidation, export to MS Project / Excel |

---

## Azure DevOps Integration

### From backlog to schedule in minutes

NXProject imports the full hierarchy of your project directly from Azure DevOps:

```
Project → Epic → Feature → Story
```

Each Story becomes a schedule row with start date, working-day duration, assignee, and sprint — all extracted from the fields your team already fills in DevOps.

### DevOps Project List

Manage multiple DevOps projects in a shared file across your team. Each project has a name and root ID; to import, just pick the project from the list — no need to remember the ID manually.

### What is read automatically

- **Hierarchy**: `Project → Epic → Feature → Story` via `Child` links
- **Estimates**: custom `HH Estimado` field → duration in working days on the project calendar
- **Dates**: `Data_Inicio` and `Data_Fim` when already set in DevOps
- **Assignee**: `System.AssignedTo` → project resource
- **Sprint**: `System.IterationPath` → sprint association in NXProject
- **Backlog order**: `Microsoft.VSTS.Common.StackRank`
- **Blockers**: child Tasks with the `Block` tag mark the Story as blocked
- **State**: `Closed`/`Resolved` Stories with open child Tasks are flagged and auto-corrected
- **Allocation %**: `Perc_Alocacao` — how much of the person's day is dedicated to this Story (affects finish date)
- **Sync version**: `Sync_version` and `Sync_Name` — concurrency control (see below)

> Field names can be changed in the **Advanced fields** section of the import dialog if your process uses different names.

---

### Required custom fields (Story, Feature and Epic)

NXProject reads and writes custom fields on **Stories, Features and Epics** in Azure DevOps. You must create them in your process template under **Organization Settings → Process → [Your Process]** and add them to each work item type you want to sync (Story, Feature, Epic).

| Field name (display) | Reference name | Type | Default in NXProject | Used on | Purpose |
|---|---|---|---|---|---|
| `HH Estimado` | `Custom.HHEstimado` *(example)* | Integer or Decimal | `HH Estimado` | Story, Feature, Epic | Estimated effort in hours |
| `Data_Inicio` | `Custom.DataInicio` *(example)* | Date/Time | `Data_Inicio` | Story, Feature, Epic | Planned start date |
| `Data_Fim` | `Custom.DataFim` *(example)* | Date/Time | `Data_Fim` | Story, Feature, Epic | Planned finish date |
| `Perc_Alocacao` | `Custom.PercAlocacao` *(example)* | Integer (1–100) | `Perc_Alocacao` | Story | % of person's day dedicated to this Story |
| `Perc_Conclusao` | `Custom.PercConclusao` *(example)* | Integer (0–100) | `Perc_Conclusao` | Story | % completion (read on import, written on sync) |
| `Sync_version` | `Custom.Syncversion` *(example)* | Integer | `Sync_version` | Story, Feature, Epic | Concurrency version counter (auto-managed) |
| `Sync_Name` | `Custom.SyncName` *(example)* | Text *(plain text, not Identity)* | `Sync_Name` | Story, Feature, Epic | Who last synced (auto-managed) |

> The reference names above are examples — Azure DevOps generates them automatically from the display name and your organization prefix.  
> If your fields have different display names, update them in NXProject under **File → Import TFS / Azure DevOps → Advanced fields** expander.

> **Tip:** create the fields once at the process level and then add them to Story, Feature and Epic work item types — they share the same field definition across types.

#### Concurrency control (`Sync_version` / `Sync_Name`)

When two users sync changes simultaneously, the last write could overwrite the first. NXProject prevents this with the `Sync_version` / `Sync_Name` pair, which must be present on every work item type you sync (Story, Feature, Epic):

- On every sync that writes at least one change, `Sync_version` is incremented by 1 and `Sync_Name` is set to the current Windows user.
- When you sync, NXProject compares the version it read during import with the current version in DevOps. If the DevOps version is higher, someone else saved more recently — the item is **skipped** and marked in **red** in the schedule.
- Red items remain highlighted until you re-import the project. The sync log shows which items had conflicts.
- Clicking a red item in the state column opens the DevOps link window, which displays a conflict warning with a **↓ Re-import** button to start the import directly.
- The version counter resets to 1 after reaching the integer limit.

> **`Sync_Name` must be plain text, not Identity type.** If you created it as an Identity (person picker) field, delete it and recreate it as **Text (single line)**.

### Import log

When importing, NXProject generates a report with:
- Stories whose state was auto-corrected (e.g., closed Story with open Task)
- Predecessors pointing to items outside the imported scope
- Warnings and inconsistencies to review before publishing the schedule

### Sync back to DevOps

After adjusting dates, dependencies, and estimates in the schedule, NXProject syncs the changes back to Azure DevOps: title, description, hours, dates, state, tags, sprint, and predecessor links.

### Open work items directly in DevOps

On any linked task, the **"Open in DevOps ↗"** button opens the work item in the browser. The link window also shows the list of child Tasks with ID, name, and state — for quick reference without leaving NXProject.

---

## Usability

- **Interactive Gantt chart** with zoom by day, sprint, or custom period
- **Task dependencies** (predecessors), including across Stories from different Epics
- **Resource allocation**: workload view per person and period
- **Project Health Check**: lists delayed tasks and tasks with no assignee
- **Configurable calendar**: holidays, working hours per day, weekdays
- **Export**: MS Project XML, OpenProj, Excel XML, CSV, **PDF (landscape)**
- **AI Assistant** for task structure suggestions
- **Tech Lead window**: fetch, create and edit DevOps Tasks per Story; cascade Epic → Feature → Story selection from the toolbar, or open directly from a Story's context menu
- **TKs column** (expanded mode): shows the count of DevOps child Tasks per Story — red when zero, so Stories with no technical tasks are immediately visible
- **Custom DevOps Fields**: configurable classification fields per work item type (Epic, Feature, Story); values are read on import and editable via right-click context menu
- **Double-click to edit** task names, preventing accidental edits when navigating the grid
- **Multilingual**: Portuguese (Brazil) and English, auto-detected from Windows, switchable in Settings

---

## Build from source

Prerequisites: [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) and [VS Code](https://code.visualstudio.com/download).

```powershell
# Set up environment
.\setup-community-vscode.ps1

# Build
.\build-community.ps1 -Configuration Release

# Or generate the self-contained distribution zip
.\release-community-new-version.ps1 -Configuration Release
```

The development executable will be generated in `NXProject.Community\bin\Release\net10.0-windows\`.

> **Important — generating the official release `.exe`**
>
> Always use `dotnet publish --self-contained true -r win-x64` (or the project's release script, which already does this).
> If you use only `dotnet build`, the resulting `.exe` may fail on machines with a broken .NET registry entry, showing a misleading error such as:
>
> ```
> To run this application, you must install .NET.
> ```
>
> …even when `dotnet --list-runtimes` shows .NET installed correctly.
> The self-contained package bundles the .NET runtime inside the ZIP and avoids this issue entirely.

---

## Configure Azure DevOps

### Personal Access Token

1. In Azure DevOps, click the user icon → **Personal access tokens**
2. Click **New Token**
3. Under **Scopes**, select **Work Items → Read** (add **Write** if you want to sync back)
4. Copy the token and paste it in the import screen in NXProject

The token can be saved locally encrypted with Windows credentials (DPAPI).

### Working calendar

Configure holidays, working hours per day, and weekdays under **View → Calendar...**  
Default is 8 hours per day, Monday through Friday.

---

## License and contact

- **Company**: Nexus XData Tecnologia Ltda
- **Commercial contact**: `comercial.nexus.xdata@gmail.com`

NXProject uses an **Open Core / dual licensing** model:

| Edition | Use |
|---|---|
| **Community (free)** | Free for individuals and companies, including internal commercial use, unlimited users. Free redistribution allowed with credit to Nexus XData. |
| **Commercial / Enterprise** | No restrictions on resale or SaaS, official support, SLA, exclusive modules. Contact us for a proposal. |

> Selling, charging for, or offering NXProject as a paid service requires a commercial license.

---

## Tell us how NXProject is helping your project

If NXProject is being used at your company and making a difference — whether in schedule visibility, team management, or Azure DevOps integration — **we want to hear about it**.

Send a short message to `comercial.nexus.xdata@gmail.com` with:

- Project context (team size, industry, the challenge you had)
- What improved after you started using NXProject
- If you authorize it, we'll share the case as a reference for the community

Real testimonials help prioritize improvements, attract contributors, and show other teams that the product works in practice. **Your experience can help other projects.**
