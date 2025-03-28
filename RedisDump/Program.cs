using Spectre.Console.Cli;
using RedisDump.Commands;

var app = new CommandApp();

app.Configure(config =>
{
    config.AddCommand<DumpCommand>("dump")
        .WithDescription("Dumps Redis data to a file")
        .WithExample(new[] { "dump", "--connection", "localhost:6379,password=secret,ssl=true", "--output", "redis-backup.json" })
        .WithExample(new[] { "dump", "--connection", "localhost:6379", "--database", "2", "--output", "redis-backup.json" })
        .WithExample(new[] { "dump", "--connection", "localhost:6379", "-d", "0", "-d", "1", "-d", "2", "--output", "redis-backup.json" });
        
    config.AddCommand<RestoreCommand>("restore")
        .WithDescription("Restores Redis data from a file")
        .WithExample(new[] { "restore", "--connection", "localhost:6379,password=secret,allowAdmin=true", "-d", "0", "--input", "redis-backup.json" })
        .WithExample(new[] { "restore", "--connection", "localhost:6379", "--input", "redis-backup.json", "--force" })
        .WithExample(new[] { "restore", "--connection", "localhost:6379", "--input", "redis-backup.json", "--flush", "--force" });
});

return await app.RunAsync(args); 