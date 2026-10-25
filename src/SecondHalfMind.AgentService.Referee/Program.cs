using SecondHalfMind.Agents.Hosting;

await AgentServiceHost.Build<SecondHalfMind.Agents.PredictionRefereeAgent>(args).RunAsync();
