using SecondHalfMind.Agents.Hosting;

await AgentServiceHost.Build<SecondHalfMind.Agents.StatsAgent>(args).RunAsync();
