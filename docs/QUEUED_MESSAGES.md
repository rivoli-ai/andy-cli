# Queued messages

Messages submitted while the agent is working are accepted after all tools in the current or next tool-call round complete, before the next model request. They steer the existing Engine turn and remain in its saved transcript. Each boundary takes a FIFO snapshot; input arriving after that handoff waits for another boundary. If the agent finishes without another tool round, remaining messages start subsequent turns.

Recall a queued message with Ctrl+Up/Down in the normal composer, or Up/Down in Prompt History mode (Ctrl+] toggles that mode). Recall removes it from the queue immediately, marks its bubble removed, and restores its image attachment to the composer. Edit and submit to send it again, or clear the composer to discard it. A message already taken by the Engine cannot be recalled as unsent; the composer reports that the recalled text is a new draft.

File mentions resolve at handoff. Images remain the snapshots selected by the user. Cancellation or failure while preparing a batch restores it ahead of newer arrivals. Editing/removal and boundary consumption are atomic; pending input does not interrupt an in-progress tool or bypass tool permissions or Engine budgets.

Completed 2026-09-08: tool-round steering, transcript preservation, atomic recall/removal, canceled preparation recovery, and queue/pump shutdown synchronization (issue #299).
