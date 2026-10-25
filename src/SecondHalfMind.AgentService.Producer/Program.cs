using SecondHalfMind.Agents.Hosting;

await AgentServiceHost.Build<SecondHalfMind.Agents.ProducerAgent>(args).RunAsync();
