using System.Text;
using GodMode.AI.Tools;

namespace GodMode.Voice.AI;

public static class SystemPromptBuilder
{
    public static string Build(ToolRegistry registry, VoiceContextSummary? context = null)
    {
        var sb = new StringBuilder();

        sb.AppendLine("You are GodMode Voice Assistant. You control a development system that manages Claude Code instances across servers.");
        sb.AppendLine("You ALWAYS respond with exactly one JSON tool call. Never respond with plain text.");
        sb.AppendLine($"Current date and time: {DateTime.Now:dddd, MMMM d, yyyy 'at' h:mm tt}");
        sb.AppendLine();

        // Dynamic context
        if (context is not null)
        {
            sb.AppendLine("CURRENT CONTEXT:");
            sb.AppendLine($"- Active profile: {context.ActiveProfile ?? "All (showing everything)"}");
            sb.AppendLine($"- Active server: {context.ActiveServer ?? "Auto (first available)"}");
            sb.AppendLine($"- Focused project: {context.FocusedProject ?? "None"}");
            sb.AppendLine($"- Projects available: {context.ProjectCount}");
            sb.AppendLine();

            if (context.FocusedProject is not null)
            {
                sb.AppendLine("FOCUS MODE:");
                sb.AppendLine($"You are focused on project \"{context.FocusedProject}\". When the user says something conversational");
                sb.AppendLine("that sounds like a response to the project (e.g., \"yes\", \"approve that\", \"go ahead\", \"no don't do that\"),");
                sb.AppendLine("route it as: {\"tool\": \"send_input\", \"arguments\": {\"message\": \"<their message>\"}}");
                sb.AppendLine("The project_name is not needed when focused — the system fills it automatically.");
                sb.AppendLine();
            }
        }

        sb.AppendLine("TOOLS:");
        foreach (var tool in registry.Tools.Values)
        {
            sb.AppendLine($"- {tool.Name}: {tool.Description}");
            if (tool.Parameters.Count > 0)
            {
                foreach (var p in tool.Parameters)
                {
                    var req = p.Required ? "required" : "optional";
                    sb.AppendLine($"    \"{p.Name}\" ({req}): {p.Description}");
                }
            }
        }

        sb.AppendLine();
        sb.AppendLine("PARAMETER RULES:");
        sb.AppendLine("- ALWAYS call the action tool directly with whatever parameters the user provided.");
        sb.AppendLine("- Do NOT use the respond tool to ask for missing parameters. The system handles that automatically.");
        sb.AppendLine("- If the user provides no parameters, call the tool with an empty arguments object.");
        sb.AppendLine("- Map user words to the correct parameter names carefully.");
        sb.AppendLine("- Profile and server context is tracked automatically. Most tools do not need explicit profile/server args.");
        sb.AppendLine();
        sb.AppendLine("OUTPUT FORMAT — always respond with ONLY this JSON, nothing else:");
        sb.AppendLine("{\"tool\": \"TOOL_NAME\", \"arguments\": {\"key\": \"value\"}}");
        sb.AppendLine();
        sb.AppendLine("EXAMPLES:");
        sb.AppendLine("User: Hello! → {\"tool\": \"respond\", \"arguments\": {\"message\": \"Hello! I can manage your profiles, servers, and projects. Try saying 'list profiles' or 'list projects'.\"}}");
        sb.AppendLine("User: What profiles do I have? → {\"tool\": \"list_profiles\", \"arguments\": {}}");
        sb.AppendLine("User: Switch to admin → {\"tool\": \"set_profile\", \"arguments\": {\"profile_name\": \"admin\"}}");
        sb.AppendLine("User: Show all profiles → {\"tool\": \"set_profile\", \"arguments\": {\"profile_name\": \"all\"}}");
        sb.AppendLine("User: Show me the servers → {\"tool\": \"set_server\", \"arguments\": {}}");
        sb.AppendLine("User: Use local server → {\"tool\": \"set_server\", \"arguments\": {\"server_name\": \"local\"}}");
        sb.AppendLine("User: List projects → {\"tool\": \"list_projects\", \"arguments\": {}}");
        sb.AppendLine("User: How is Demo doing? → {\"tool\": \"project_status\", \"arguments\": {\"project_name\": \"Demo\"}}");
        sb.AppendLine("User: Focus on Demo → {\"tool\": \"focus_project\", \"arguments\": {\"project_name\": \"Demo\"}}");
        sb.AppendLine("User: Unfocus → {\"tool\": \"unfocus_project\", \"arguments\": {}}");
        sb.AppendLine("User: Create a new project → {\"tool\": \"create_project\", \"arguments\": {}}");
        sb.AppendLine("User: Tell it yes → {\"tool\": \"send_input\", \"arguments\": {\"message\": \"yes\"}}");
        sb.AppendLine("User: Stop Demo → {\"tool\": \"stop_project\", \"arguments\": {\"project_name\": \"Demo\"}}");
        sb.AppendLine("User: Resume Demo → {\"tool\": \"resume_project\", \"arguments\": {\"project_name\": \"Demo\"}}");

        return sb.ToString();
    }
}
