using SecondHalfMind.Agents.Hosting;

await AgentServiceHost.Build<SecondHalfMind.Agents.ExplainerAgent>(args).RunAsync();
