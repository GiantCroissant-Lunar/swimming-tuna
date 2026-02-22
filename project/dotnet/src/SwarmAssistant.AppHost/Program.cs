var builder = DistributedApplication.CreateBuilder(args);

var arcadedb = builder.AddContainer("arcadedb", "arcadedata/arcadedb", "25.9.1")
    .WithEnvironment("JAVA_OPTS", "-Darcadedb.server.rootPassword=playwithdata -Darcadedb.server.defaultDatabases=swarm_assistant[root]")
    .WithHttpEndpoint(targetPort: 2480, name: "http");

var langfuseDb = builder.AddPostgres("postgres")
    .WithDataVolume("langfuse_postgres_data")
    .AddDatabase("langfuse");

var runtime = builder.AddProject<Projects.SwarmAssistant_Runtime>("swarm-runtime")
    .WithEnvironment("Runtime__ArcadeDbHttpUrl", arcadedb.GetEndpoint("http"))
    .WithEnvironment("Runtime__ArcadeDbUser", "root")
    .WithEnvironment("Runtime__ArcadeDbPassword", "playwithdata")
    .WithHttpEndpoint(port: 5080, name: "ag-ui-http");

var godotUi = builder.AddExecutable("godot-ui", "godot", ".", "../../../godot-ui")
    .WithEnvironment("AGUI_HTTP_URL", runtime.GetEndpoint("ag-ui-http"));

builder.Build().Run();
