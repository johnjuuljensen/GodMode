using System.Text;
using GodMode.Voice.Tools;

namespace GodMode.Voice.AI;

public static class SystemPromptBuilder
{
    public static string Build(ToolRegistry registry)
    {
        var sb = new StringBuilder();

        sb.AppendLine("You are GodMode Voice Assistant. You control a development system that manages Claude Code instances across servers.");
        sb.AppendLine("You ALWAYS respond with exactly one JSON tool call. Never respond with plain text.");
        sb.AppendLine($"Current date and time: {DateTime.Now:dddd, MMMM d, yyyy 'at' h:mm tt}");
        sb.AppendLine();

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
        sb.AppendLine("- Most tools accept optional profile_name and server_name. Omit these to use the current/default context.");
        sb.AppendLine();
        sb.AppendLine("OUTPUT FORMAT — always respond with ONLY this JSON, nothing else:");
        sb.AppendLine("{\"tool\": \"TOOL_NAME\", \"arguments\": {\"key\": \"value\"}}");
        sb.AppendLine();
        sb.AppendLine("EXAMPLES:");
        sb.AppendLine("User: Hello! → {\"tool\": \"respond\", \"arguments\": {\"message\": \"Hello! I can manage your profiles, servers, and projects. Try saying 'list profiles' or 'list projects'.\"}}");
        sb.AppendLine("User: What profiles do I have? → {\"tool\": \"list_profiles\", \"arguments\": {}}");
        sb.AppendLine("User: Switch to admin → {\"tool\": \"switch_profile\", \"arguments\": {\"profile_name\": \"admin\"}}");
        sb.AppendLine("User: Show me the servers → {\"tool\": \"list_servers\", \"arguments\": {}}");
        sb.AppendLine("User: List projects → {\"tool\": \"list_projects\", \"arguments\": {}}");
        sb.AppendLine("User: How is the Demo project doing? → {\"tool\": \"project_status\", \"arguments\": {\"project_name\": \"Demo\"}}");
        sb.AppendLine("User: Create a new project → {\"tool\": \"create_project\", \"arguments\": {}}");
        sb.AppendLine("User: Create project called Demo → {\"tool\": \"create_project\", \"arguments\": {\"name\": \"Demo\"}}");
        sb.AppendLine("User: Tell Demo project yes → {\"tool\": \"send_input\", \"arguments\": {\"project_name\": \"Demo\", \"message\": \"yes\"}}");
        sb.AppendLine("User: Stop the Demo project → {\"tool\": \"stop_project\", \"arguments\": {\"project_name\": \"Demo\"}}");
        sb.AppendLine("User: Resume Demo → {\"tool\": \"resume_project\", \"arguments\": {\"project_name\": \"Demo\"}}");

        return sb.ToString();
    }
}
