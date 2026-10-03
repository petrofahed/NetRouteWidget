# NetRoute Widget v4 — Smart routing profiles (design)

**Status:** Draft for review. Design approved in conversation on 2026-10-03; this file is the written spec to approve before a plan is written.
**Builds on:** v3 (`feat/v2-smart-routing`): Usage tab, live speed on the card, running-balance accounting.

## Goal

Two Smart routing **profiles**, switched in one click, so the user can choose which connection carries "everything else":

| Profile | Default exit (everything not listed) | Exceptions (listed items) go through |
|---|---|---|
| **Phone + exceptions** | Phone | LAN (today's behaviour, unchanged) |
| **LAN + exceptions** | LAN | Phone |

In **LAN + exceptions** the exceptions are the AI tools and the user's ISP site, which must keep working through the phone: Claude / Claude Code, OpenAI / ChatGPT, Codex, Visual Studio Code, GitHub Copilot, Gemini, Cursor, Perplexity, and everything under `omantel.om` (`*.omantel.om`).

## Decisions made (from the conversation)

1. **A profile also sets the routing mode.** Choosing *Phone + exceptions* switches the card to **Phone** mode; choosing *LAN + exceptions* switches it to **LAN** mode. The card's Phone / LAN buttons are the profile switch. **Auto** keeps today's meaning (Smart routing pauses, the widget steps aside).
2. **Each profile has its own exception list.** The existing list (built-in items + user rules that go to the LAN) belongs to *Phone + exceptions* and is untouched. *LAN + exceptions* gets a second list whose items go to the phone.
3. **The starting phone list** is built in and each entry is switchable (all ON by default): Claude, OpenAI / ChatGPT, Codex, Visual Studio Code, GitHub Copilot, Gemini, Cursor, Perplexity, Omantel. The user can add their own apps/websites to either list.
4. **Updates are separate from the work, and go through the LAN.** VS Code, Codex and Claude each get their own "updates" item, apart from the main work item. Update items always route to the **LAN** (big downloads stay off mobile data), in both profiles; the work items (the app itself and its AI calls) go to the phone in *LAN + exceptions*. In *Phone + exceptions* the update items are ordinary LAN exceptions (kept off 4G).
5. **Config tab** gets an "Editing: Phone + exceptions | LAN + exceptions" selector. The active profile is marked; the other profile's list can be edited without switching.

## Behaviour

- **Active exceptions follow the mode.** Mode Phone → the LAN list is active (items → LAN, waiting for the LAN if it is unplugged, as today). Mode LAN → the phone list is active (items → phone, always). The inactive list is stored and shown but produces no sing-box rules.
- **Update items are carve-outs and are active in both profiles.** They are LAN-exit items flagged `"carveOut": true`. In *LAN + exceptions* they are active together with the phone list and are matched **before** it, so e.g. a VS Code update download (domain rule → LAN) wins over the `code.exe` process rule (→ phone). In *Phone + exceptions* they are plain LAN items. Carve-outs are domain-only (no process match), so they apply to whichever process fetches the update. Like any LAN-exit rule they wait for the LAN if it is unplugged (a big download must not fall onto 4G).
- **LAN + exceptions, LAN unplugged / no gateway:** unmatched traffic falls back to the phone exactly as LAN mode does today (`WantedExit`); phone exceptions are unaffected. No waiting popup (nothing waits for the LAN in this profile).
- **Switching profile = mode change.** The rule set changes, so sing-box restarts for about a second, same as a rule change. Open connections move (the selectors already interrupt existing connections).
- **Phone lost:** unchanged — Smart routing becomes Unavailable and v1 falls back to the LAN.
- **Existing users:** no migration. Settings and rules saved today are the *Phone + exceptions* list. New built-in phone items appear ON.

## Data model

- `rules/builtin.json`: a group may carry `"exit": "phone"` (default `"lan"`). All items in it route to that exit when the group's profile is active. New groups: **AI tools** (Claude, OpenAI/ChatGPT, Codex, VS Code, Copilot, Gemini, Cursor, Perplexity) and **Omantel** (`omantel.om`).
- A group or item may also carry `"carveOut": true` (only meaningful on LAN-exit items): active in both profiles, ordered before the phone rules. `RuleItem` gains `CarveOut` (default false).
- `RuleItem` gains `Exit` (`RouteExit`, default `Lan`). Item ids stay globally unique, so the existing `Items` on/off dictionary in `SmartRoutingSettings` serves both lists.
- `UserRule` gains `Exit` (default `Lan`; a settings file without it loads as `Lan`). A rule's identity is (type, value, exit), so the same app can be an exception in both profiles. `IdOf` includes the exit for phone rules (`user:phone:app:x.exe`), unchanged for LAN rules.
- `RuleEntry` gains `Exit`; it is part of `RuleSet.Fingerprint`, so a profile switch restarts sing-box.
- `RuleSet.Build(catalog, settings, profileMode)` keeps the entries whose `Exit` is the **opposite** of the profile's default exit (profile Phone → entries with exit LAN; profile LAN → entries with exit Phone) plus, in the LAN profile, the carve-out entries; carve-outs are listed first. `BuildAll` still returns everything, both exits (see Usage).
- No new persisted "profile" field: the profile **is** `RoutingMode` (Phone / Lan) already saved by v1.
- Not persisted: which list the Config selector is editing.

## sing-box config

- LAN-exit rules: unchanged (`outbound: lan-only`, the selector that can wait for the LAN).
- Phone-exit rules: `outbound: phone` (the existing direct outbound bound to the phone adapter). Same process-path-regex and domain-suffix forms, same separate rules for processes and domains.
- `final: default`, whose selector follows `WantedExit` as now. Private ranges stay on the LAN, DNS hijack unchanged.
- Rule order: carve-out (update) rules first, then the other exceptions, all before `final`. sing-box takes the first match, so a carve-out beats a phone process rule for the same connection.

## Controller

- `ApplyAsync` rebuilds `_rules` from (settings, `net.Mode`) every time, so a mode change that arrives through the card buttons updates the rules and, through the fingerprint, restarts sing-box.
- `LanWaitTracker` and the "waiting for LAN" popup only consider LAN-exit entries.
- `Desired()` is unchanged: Phone and LAN modes run Smart routing, Auto pauses it.
- `SmartRoutingStatus` gains the active profile (`Phone` | `Lan`) so the card and Config tab can name it.

## UI

- **Card:** the Phone / LAN / Auto buttons are unchanged and are the profile switch. Tooltips name the profiles ("Phone + exceptions", "LAN + exceptions"). The Smart row shows the active profile, e.g. `⚡ Smart routing ON · LAN + exceptions · 9 rules`.
- **Config tab:** a two-way selector at the top, "Editing: Phone + exceptions | LAN + exceptions" (default: the active profile; the active one is marked "active"). The group list, per-item switches, "Add app / website" and the day figures use the selected profile's list. Wording follows the exit: "goes through LAN" / "goes through phone". Group switches show off/mixed/on as today.
- **Usage tab:** unchanged. Rows resolve against **all** rules of both profiles, so a row does not change name when the profile is switched (a phone-exception item, e.g. Claude, keeps its own row in both profiles). The Phone and LAN columns already show where bytes went.
- "Kept off 4G" keeps its meaning (LAN bytes caused by LAN-exit rules while the phone is the default). In *LAN + exceptions* it is 0. A "phone data used by exceptions" figure is deliberately **not** built now.

## Starting catalog (best guesses — to verify on the machine)

**Work items → phone (LAN + exceptions list):**

| Item | Processes | Domains |
|---|---|---|
| Claude / Claude Code | `claude.exe` | `claude.ai`, `anthropic.com` |
| OpenAI / ChatGPT | `chatgpt.exe` | `openai.com`, `chatgpt.com`, `oaistatic.com`, `oaiusercontent.com` |
| Codex | `codex.exe` | — |
| Visual Studio Code | `code.exe` | — |
| GitHub Copilot | — | `githubcopilot.com`, `copilot.microsoft.com` |
| Gemini | — | `gemini.google.com`, `aistudio.google.com`, `generativelanguage.googleapis.com` |
| Cursor | `Cursor.exe` | `cursor.sh`, `cursor.com` |
| Perplexity | — | `perplexity.ai` |
| Omantel | — | `omantel.om` (matches every `*.omantel.om`) |

**Update items → LAN, carve-outs (active in both profiles), group "AI & dev tool updates":**

| Item | Domains |
|---|---|
| VS Code updates | `update.code.visualstudio.com`, `vscode.download.prss.microsoft.com`, `az764295.vo.msecnd.net`, `marketplace.visualstudio.com`, `gallerycdn.vsassets.io` |
| Claude updates | `downloads.claude.ai` |
| Codex updates | `registry.npmjs.org`, `release-assets.githubusercontent.com`, `objects.githubusercontent.com` |

Notes: the update domains are the weakest guesses (installers and CDNs change, and the GitHub/npm hosts are shared with other downloads — those also go through the LAN, which is harmless for downloads). The on-machine check uses the Usage tab and the sing-box log to confirm what each updater really contacts. Visual Studio Code's *other* traffic (extensions' own calls, settings sync) still goes through the phone in this profile, as asked; the item can be switched off.

## Testing

Unit tests (Core, no network): catalog parses `exit` (default LAN, rejects an unknown value); `UserRule` round trip and back-compat load without `Exit`; `RuleSet.Build` picks the right list per mode and `BuildAll` returns both; fingerprint changes with exit; config builder emits `outbound: phone` for phone entries and keeps `lan-only` for LAN entries; carve-outs are active in both modes and come first in the generated rules (a `code.exe` phone rule never captures a VS Code update domain); controller restarts on a Phone→LAN mode change and not on an unrelated change, and builds no `LanWaitTracker` entries for phone rules; presenter/page model for each profile; usage attribution resolves phone items in both profiles. App layer is checked with the render harness (Config selector) plus the on-machine checklist: switch both ways with the real widget and confirm Claude / Codex / VS Code traffic shows under Phone in the Usage tab while everything else shows under LAN, and `*.omantel.om` goes through the phone.

## Out of scope

More than two profiles or custom profile names; automatic switching by network; per-profile LAN-fallback tuning; the "phone data used by exceptions" counter; migrating usage history rows (old `app:codex.exe` history stays under its old key).

## Risks / open points

- The catalog process names and domains above are best guesses; the on-machine check decides.
- Switching the mode now also swaps the rule set, so a mode change restarts sing-box for about a second (previously only rule changes did). Open downloads on the old exit are reset.
- The carve-out ordering is what keeps updates off 4G; a mistake in a domain guess sends that update through the phone (or, for shared hosts, more through the LAN than intended). Both are visible in the Usage tab.
- `claude.exe` is both the desktop app and the Claude Code binary; both go to the phone together.
