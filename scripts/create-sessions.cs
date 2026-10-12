using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
var n = int.Parse(args.ElementAtOrDefault(0) ?? "3");
var c = new HubConnectionBuilder().WithUrl("http://127.0.0.1:31374/hubs/projects", o => o.Headers["Authorization"] = "Bearer test274key-not-secret-0123456789").Build();
await c.StartAsync();
for (var i = 0; i < n; i++)
{
    var inputs = new Dictionary<string, JsonElement> { ["name"] = JsonSerializer.SerializeToElement($"s{i}-{DateTime.Now:HHmmss}"), ["prompt"] = JsonSerializer.SerializeToElement("go") };
    var r = await c.InvokeAsync<JsonElement>("CreateProject", "test274", "t274", null, inputs);
    Console.WriteLine(r);
}
await Task.Delay(3000);
Console.WriteLine(await c.InvokeAsync<JsonElement>("GetAttention"));
