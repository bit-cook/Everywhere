# Matching System & Evaluation

When a user activates the Assistant, the Strategy Engine enters its matching and evaluation loop. This happens instantly, gathering the OS snapshot and assessing which Strategies apply.

## 1. Context Gathering (StrategyContext)

The `StrategyContext` comprises the current computing state, including:
*   **Attachments**: User-provided inputs (selected text, visual bounding boxes, clipboard contents).
*   **Root Elements**: Reconstructed UI automation trees to search for window metadata or element text.
*   **Active Process**: The application currently in focus (e.g., `chrome.exe`, `devenv.exe`).

## 2. Evaluation Pipeline

The matching process follows several distinct phases:

### Phase 1: Strategy Collation
The engine requests `GetStrategies()` from all activated Providers, gathering an initial pool of all available Strategies from the OS, builtin defaults, and user directories.

### Phase 2: Deduplication & Conflict Resolution
Because Strategies are flat, they are identified by a globally unique `Id` (often prefixed by their namespace like `user.summarize-arxiv` or `builtin.explain`).

If the engine encounters multiple Strategies with the exact same `Id` across different providers, it resolves conflicts based on the `Priority` integer field.
*   **Highest Priority Wins**: A user definition of `builtin.summarize-arxiv` with a priority of `100` implicitly **overwrites** the system default (priority `10`).

This acts as an elegant hot-swapping/overriding mechanism, removing the need for hardcoded patches.

### Phase 3: Condition Evaluation
After deduplication, the engine runs `.Evaluate(Context)` on the individual `Condition` tree defined by each Strategy.

Conditions can be simple text regex matches, process name checks (`"chrome"`), deep OS attachment inspections, or complex generic UI queries.
*   If `Evaluate()` returns true -> Keep.
*   If `Evaluate()` returns false -> Drop.

### Phase 4: Sorting & Rendering
The final filtered list of Strategies is sorted by `Priority` descending. The UI then binds to this list, displaying the correct icons, names, and descriptions to the user.

## 3. The Condition Tree

Conditions support composite logic (AND, OR, NOT). Since they are declarative (most defined in YAML), non-programmers can author complex visual matching rules.

Example condition scenarios:
*   Show this button ONLY IF the active process is `idea.exe` OR `rider.exe` AND text is selected.
*   Show this button ONLY IF the UI element tree contains a control with `{AutomationId: 'AddressBar'}` (typically browsers).
