# App health and TODO

Living document: update it at the end of every work session. Status marks: **[x]** done and verified, **[~]** done but only partly verified, **[ ]** not done.

## v6.2 local source evidence — 2026-10-30

- **[x] T4-BIND1, source finding:** architect review of `19486d37a7adaf1366487875bcba1b4291f3c805` found that choosing a missing explicit LAN B could reseed saved LAN A on every Apply; a cached-name collision also omitted physically present off adapters. This was a reproduced source defect, not an observed live-widget incident. Fixed in `7c0b56cf508d2238a331934e50d4625f24728c39`: persisted names seed only the legacy automatic choice; explicit choices wait for their own live observation; collision checks include OffAdapters. Three regressions failed before the fix, all133 controller tests passed after, and executor full1224 passed with app0warnings/errors. Logs: `F:/source/petrofahed/pr-team-evidence/v6-2-20261030/task4-bind-{red,green,full-unit,app-build}.log`; architect closed the finding. Other planned TDD compile/failure probes are test evidence, not production incidents.
- **[~] v6.2 source:** Tasks1-5 internally approved; latest feature SHA `92f32291c7bffd38b4957adead24be2c4adea2ac`, full1243 passed0failed0skipped and app-task5 build0warnings/errors. Final version/docs commit requires its own exact-SHA check/review. No actual WPF/TUN/network acceptance or release/deployment is claimed.
- **[ ] Owner runtime:** PR5 G1 Integration and G2 widget checks remain unreported; the owner-directed merge did not prove either passed. v6.2 runtime matrix is prepared separately for the owner's elevated session. No team process/network/Integration actions were taken.
- **[ ] Later phases:** v6.3 three-bucket usage/live speed/speed test; v6.4 Wi-Fi picker. Existing Phone/LAN usage schema is intentional in v6.2. Prior low-priority F3 own-toast multi-refresh follow-up remains outside this package.
