namespace MateMCP.Relay;

public static class McpScopePolicy
{
    public const string UnsupportedScope = "mcp:unsupported";

    public static string RequiredScopeForTool(string? toolName) => toolName switch
    {
        // Read-only filesystem, configuration discovery, durable context, transfer status,
        // and visual/semantic inspection.
        "filesystem_projects" or
        "filesystem_list" or
        "filesystem_read" or
        "secret_list" or
        "agent_file_upload_status" or
        "project_list" or
        "project_get" or
        "project_resolve" or
        "memory_search" or
        "memory_applicable" or
        "memory_read" or
        "screen_list" or
        "window_list" or
        "screen_capture" or
        "ui_snapshot" or
        "browser_wait_for" or
        "browser_snapshot" or
        "browser_screenshot" or
        "browser_diagnostics" or
        "visual_viewports" or
        "visual_capture" or
        "visual_compare" => "mcp:read",

        // Mutations constrained to MateMCP-managed project/configuration/content state.
        "filesystem_write" or
        "agent_file_upload_start" or
        "agent_file_upload_chunk" or
        "agent_file_upload_complete" or
        "agent_file_upload_cancel" or
        "project_register" or
        "project_update" or
        "project_unregister" or
        "memory_create" or
        "memory_update" or
        "memory_delete" => "mcp:write",

        // General process execution plus browser/desktop control can act outside a
        // project write boundary, so require the strongest existing host-control scope.
        "shell_exec" or
        "shell_session_start" or
        "shell_session_read" or
        "shell_session_write" or
        "shell_session_send_secret" or
        "shell_session_close" or
        "mouse_move" or
        "mouse_click" or
        "mouse_drag" or
        "mouse_scroll" or
        "keyboard_type" or
        "keyboard_press" or
        "keyboard_shortcut" or
        "window_focus" or
        "ui_click" or
        "ui_type" or
        "ui_focus" or
        "ui_toggle" or
        "ui_select" or
        "ui_expand" or
        "ui_click_at" or
        "ui_scroll_into_view" or
        "browser_open" or
        "browser_click" or
        "browser_fill" or
        "browser_set_viewport" or
        "browser_reload" or
        "browser_back" or
        "browser_forward" or
        "browser_close" or
        "browser_select" or
        "browser_press" or
        "browser_check" => "mcp:shell",

        _ => UnsupportedScope
    };
}
