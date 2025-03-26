using Spectre.Console;
using Spectre.Console.Cli;
using RedisDump.Commands;

var app = new CommandApp();

app.Configure(config =>
{
    config.AddCommand<DumpCommand>("dump")
        .WithDescription("Dumps Redis data to a file")
        .WithExample(new[] { "dump", "--connection", "localhost:6379", "--output", "redis-backup.json" });
        
    config.AddCommand<RestoreCommand>("restore")
        .WithDescription("Restores Redis data from a file")
        .WithExample(new[] { "restore", "--connection", "localhost:6379", "--input", "redis-backup.json" });
});

return await app.RunAsync(args);