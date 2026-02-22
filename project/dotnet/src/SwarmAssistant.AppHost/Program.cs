var builder = DistributedApplication.CreateBuilder(args);

var arcadeDbPassword = builder.Configuration["arcadedb-password"] ?? "playwithdata";

var arcadedb = builder.AddContainer("arcadedb", "arcadedata/arcadedb", "25.9.1")
    .WithEnvironment("JAVA_OPTS", $"-Darcadedb.server.rootPassword={arcadeDbPassword} -Darcadedb.server.defaultDatabases=swarm_assistant[root]")
    .WithHttpEndpoint(targetPort: 2480, name: "http");

var postgres = builder.AddPostgres("postgres")
    .WithDataVolume("langfuse_postgres_data");

var langfuseDb = postgres.AddDatabase("langfuse");

var langfuse = builder.AddContainer("langfuse", "langfuse/langfuse", "3")
    .WithReference(langfuseDb)
    .WithHttpEndpoint(targetPort: 3000, name: "http");

var runtime = builder.AddProject<Projects.SwarmAssistant_Runtime>("swarm-runtime")
    .WithEnvironment("Runtime__ArcadeDbHttpUrl", arcadedb.GetEndpoint("http"))
    .WithEnvironment("Runtime__ArcadeDbUser", "root")
    .WithEnvironment("Runtime__ArcadeDbPassword", arcadeDbPassword)
    .WithEnvironment("Runtime__LangfuseBaseUrl", langfuse.GetEndpoint("http"))
    .WithHttpEndpoint(port: 5080, name: "ag-ui-http");

var godotUi = builder.AddExecutable("godot-ui", "godot", ".", "../../../../godot-ui")
    .WithEnvironment("AGUI_HTTP_URL", runtime.GetEndpoint("ag-ui-http"));

builder.Build().Run();
