using System.Text;
using GodMode.Voice.Tools;

namespace GodMode.Voice.AI;

public static class SystemPromptBuilder
{
    public static string Build(ToolRegistry registry)
    {
        var sb = new StringBuilder();

        sb.AppendLine("You are Godmode LOCA. You ALWAYS respond with exactly one JSON tool call. Never respond with plain text.");
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
        sb.AppendLine("- Map user words to the correct parameter names carefully. For example: \"create project Demo with description A test\" means name=\"Demo\" and description=\"A test\".");
        sb.AppendLine();
        sb.AppendLine("OUTPUT FORMAT — always respond with ONLY this JSON, nothing else:");
        sb.AppendLine("{\"tool\": \"TOOL_NAME\", \"arguments\": {\"key\": \"value\"}}");
        sb.AppendLine();
        sb.AppendLine("EXAMPLES:");
        sb.AppendLine("User: Hello! → {\"tool\": \"respond\", \"arguments\": {\"message\": \"Hello! How can I help you?\"}}");
        sb.AppendLine("User: What can you do? → {\"tool\": \"respond\", \"arguments\": {\"message\": \"I can check system status, switch profiles, get project schemas, and create new projects.\"}}");
        sb.AppendLine("User: Get the status → {\"tool\": \"general_status\", \"arguments\": {}}");
        sb.AppendLine("User: Switch to admin → {\"tool\": \"switch_profile\", \"arguments\": {\"profile_name\": \"admin\"}}");
        sb.AppendLine("User: Create a new project → {\"tool\": \"new_project\", \"arguments\": {}}");
        sb.AppendLine("User: Create project called Demo → {\"tool\": \"new_project\", \"arguments\": {\"name\": \"Demo\"}}");
        sb.AppendLine("User: Create project Demo with description A test project → {\"tool\": \"new_project\", \"arguments\": {\"name\": \"Demo\", \"description\": \"A test project\"}}");

        return sb.ToString();
    }
}
