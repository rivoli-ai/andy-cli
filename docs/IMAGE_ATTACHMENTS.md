# Local image attachments

Drop a local PNG, JPEG, GIF, or WebP file into a terminal that pastes its path, or paste a shell-quoted absolute path or local file:// URI. Bracketed paste can attach an image alongside an existing question. For terminals without bracketed paste, a whole image path in the composer is recognized after a short idle interval.

The composer shows the filename and size. Backspace on an empty prompt removes the attachment. Enter with only an attachment submits "Describe this image." One attachment is held per message; selecting another replaces it. Queued messages retain their own image snapshot when their text is edited.

Files must have a matching supported extension and file signature and be at most 10 MiB. Bytes are copied at selection time. Providers that explicitly implement the Engine IVisionCapableLlmProvider contract receive structured image parts through MultimodalMessage.GetAttachedParts. Other providers receive a clearly labeled file reference, not image bytes or base64 prose. The currently pinned stock providers do not implement that capability; image understanding requires a provider that implements and serializes this contract.

This supports local path input, not Kitty graphics or iTerm inline-image display escape sequences, remote uploads, or clipboard bitmap data. Bracketed text pastes preserve newlines without submitting the prompt. Pasted input is limited to 1 MiB and a truncated paste is reported.

Completed 2026-09-08: local path input, bounded attachment snapshots, composer indicator, queued ownership, and tested Engine/provider delivery.
