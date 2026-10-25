using SecondHalfMind.Agents.Hosting;

await AgentServiceHost.Build<SecondHalfMind.Agents.Vision.AutoEventingAgent>(args).RunAsync();
