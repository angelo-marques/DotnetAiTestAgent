var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.DotnetAiTestAgent_Cli>("dotnetaitestagent-cli");

builder.Build().Run();
