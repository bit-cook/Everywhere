# Core Concepts

The Strategy Engine operates on a unified model where UI presentation, matching logic, and execution templates are bundled into a single concept: the **Strategy**.

## 1. Terminology

| Term             | Definition                                                                                                                                                        |
| ---------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **Strategy**     | The core entity representing a user intent. It includes UI metadata (icon, name), activation conditions, preprocessor definitions, and the final prompt template. |
| **Provider**     | A source that supplies Strategies to the engine (e.g., built-in system definitions, user-specific directory configurations, workspace-level configs).             |
| **Condition**    | A predicate or rule tree that evaluates against the current OS and visual context to determine if a Strategy should be visible to the user.                       |
| **Context**      | The snapshot of the current state: user attachments, active processes, OS environment, and parsed visual UI trees.                                                |
| **Preprocessor** | An executable middleware that intercepts a Strategy's execution to extract dynamic data (like an active URL) and inject it into the prompt template.              |

## 2. The Strategy Model

Unlike traditional architectures that split condition matching and command execution into separate layers, the engine flattens everything into a singular `Strategy`. 

A Strategy acts as:
1. **A UI Element**: It dictates what button, icon, and description the user sees.
2. **A Gatekeeper**: It holds its own `Condition` rules to decide *when* it should appear.
3. **A Workflow Definition**: It lists `Preprocessors` to run upon activation.
4. **An AI Prompter**: It contains the `Body` (user message template) and optional system prompt overrides.

## 3. Providers (IStrategyProvider)

To support unbounded extensibility, Strategies are gathered via Providers. The Strategy Engine does not hardcode which strategies exist; instead, it asks all registered Providers to supply their items.

Common providers include:
*   **Built-In Provider**: Delivers default strategies coded directly into the application (e.g., general OS helpers).
*   **User Directory Provider**: Scans `~/.everywhere/` for user-customized `.strategy.md` configurations.
*   **Workspace Provider**: (Future) Discovers strategies specific to the current project or IDE workspace.

Providers allow the engine to remain decoupled from the storage medium and format of the strategies.
