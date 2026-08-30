# Desktop control direction

Desktop control is a post-v0.1 capability and must remain independently permissioned from filesystem and shell access.

Planned capability groups:

- screen capture / stream
- mouse move / click / scroll
- keyboard text / key / hotkey
- clipboard access
- semantic accessibility/UI automation where available

Preferred interaction order:

1. Semantic accessibility/UI automation
2. Screenshot + vision fallback
3. Raw mouse/keyboard coordinates

Security requirements:

- separate grants for screen viewing, mouse control, keyboard input, and clipboard
- obvious local indicator while remote AI control is active
- immediate local pause/kill control
- sensitive OS permission prompts and credential surfaces remain approval-gated
- every action is auditable
