using SystemdServiceController;

bool userMode = args.Contains("--user");
string[] rest = userMode ? args.Where(a => a != "--user").ToArray() : args;

await using var client = new SystemdClient(userMode);
await client.ConnectAsync();

return await new Cli(client).RunAsync(rest);
