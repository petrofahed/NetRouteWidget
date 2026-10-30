# Coordinated PR team: from requirements to completion

The owner gives the requirements to the existing lead in the main session. The lead owns continuity; the architect defines the technical steps with it. `/pr-team` starts from an existing PR. This drawing describes the instructed workflow, not evidence of a completed run.

```mermaid
flowchart TD
    OWNER["Owner: goal or requirements + PR"] --> LEAD["Lead / coordinator: takes ownership"]
    LEAD --> PLAN["Lead + architect: steps and completion criteria"]
    PLAN --> DEV["Developer: prepare or implement"]
    DEV --> EXEC["Executor: authorized checks"]
    EXEC --> ARCH["Architect: review"]
    ARCH --> INTERNAL["Internal auditor: check the package"]
    ARCH -->|"Fixes: lead assigns the batch"| DEV
    INTERNAL -->|"Fixes: lead assigns the batch"| DEV
    INTERNAL --> PACKAGE["Lead: provide reviewed package and audit prompt"]
    PACKAGE --> EXTERNAL["Lead: external audit via the host adapter"]
    EXTERNAL -->|"External provider unavailable or audit pending"| MANUAL["Owner takes package to external auditor"]
    MANUAL --> VERIFY["Internal auditor verifies the findings"]
    EXTERNAL --> VERIFY
    VERIFY -->|"Valid fixes: lead coordinates re-checks"| DEV
    VERIFY -->|"Cleared"| NEXT["Lead: identify next action"]
    NEXT -->|"Permission needed"| DECISION["Owner: decide on reviewed action"]
    DECISION --> NEXT
    NEXT -->|"Authorized work remains"| DEV
    NEXT -->|"Original goal proved complete"| DONE["Complete"]
```

The lead maintains one plan throughout. Review-round limits still apply; unresolved blockers stay visible. An external audit, a merge or a coding milestone does not establish original-goal completion. The lead uses existing permissions and brings the owner only a concrete reviewed decision when the next action exceeds them.
