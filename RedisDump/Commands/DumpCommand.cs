using System.ComponentModel;
using System.Text.Json;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Json;
using StackExchange.Redis;

namespace RedisDump.Commands;

// Record to represent Redis key data
public record RedisKeyData
{
    public required string Type { get; init; }
    public required object Value { get; init; }
    public double? TTL { get; init; }
}

public class DumpCommandSettings : SharedSettings
{
    [CommandOption("-o|--output <FILE>")]
    [Description("Output file path")]
    [DefaultValue("redis-dump.json")]
    public string OutputFile { get; set; } = "redis-dump.json";
}

public class DumpCommand : AsyncCommand<DumpCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, DumpCommandSettings settings)
    {
        AnsiConsole.MarkupLine("[bold blue]Redis Dump Tool[/]");
        
        try
        {
            // Build connection string with password if provided
            var connectionString = settings.ConnectionString;
            if (!string.IsNullOrEmpty(settings.Password))
            {
                connectionString = $"{connectionString},password={settings.Password}";
            }

            // Connect to Redis
            AnsiConsole.MarkupLine($"[yellow]Connecting to Redis at {settings.ConnectionString}...[/]");
            var connection = await ConnectionMultiplexer.ConnectAsync(connectionString);
            
            // Get server
            var server = connection.GetServer(connection.GetEndPoints().First());
            
            var result = new Dictionary<int, Dictionary<string, object>>();
            
            if (settings.Database.HasValue)
            {
                // Only dump specified database
                AnsiConsole.MarkupLine($"[yellow]Dumping database {settings.Database.Value}...[/]");
                result.Add(settings.Database.Value, await DumpDatabaseAsync(connection, settings.Database.Value, settings.Verbose));
            }
            else
            {
                // Dump all databases
                AnsiConsole.MarkupLine("[yellow]Dumping all databases...[/]");
                
                // Get the number of databases
                var databaseCount = server.DatabaseCount;
                
                await AnsiConsole.Progress()
                    .StartAsync(async ctx =>
                    {
                        var task = ctx.AddTask("[green]Dumping databases[/]");
                        task.MaxValue = databaseCount;
                        
                        for (int i = 0; i < databaseCount; i++)
                        {
                            result.Add(i, await DumpDatabaseAsync(connection, i, settings.Verbose));
                            task.Increment(1);
                        }
                    });
            }
            
            // Save to file
            AnsiConsole.MarkupLine($"[yellow]Saving to {settings.OutputFile}...[/]");
            var jsonString = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(settings.OutputFile, jsonString);
            
            AnsiConsole.MarkupLine($"[green]Successfully dumped {result.Sum(db => db.Value.Count)} keys to {settings.OutputFile}[/]");
            
            // Display preview if verbose
            if (settings.Verbose)
            {
                AnsiConsole.WriteLine();
                AnsiConsole.Write(new Rule("[yellow]JSON Preview[/]").RuleStyle("grey").Centered());
                AnsiConsole.Write(new JsonText(jsonString));
                AnsiConsole.WriteLine();
            }
            
            // Dispose connection
            connection.Dispose();
            
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[bold red]Error:[/] {ex.Message}");
            if (settings.Verbose)
            {
                AnsiConsole.WriteException(ex);
            }
            return 1;
        }
    }
    
    private async Task<Dictionary<string, object>> DumpDatabaseAsync(ConnectionMultiplexer connection, int databaseNumber, bool verbose)
    {
        var db = connection.GetDatabase(databaseNumber);
        var server = connection.GetServer(connection.GetEndPoints().First());
        
        var result = new Dictionary<string, object>();
        
        // Get all keys
        var keys = server.Keys(databaseNumber, pattern: "*").ToArray();
        
        if (verbose)
        {
            AnsiConsole.MarkupLine($"[grey]Database {databaseNumber}: Found {keys.Length} keys[/]");
        }
        
        foreach (var key in keys)
        {
            var keyString = key.ToString() ?? "";
            var type = db.KeyType(key);
            
            // Get TTL if exists
            var ttl = await db.KeyTimeToLiveAsync(key);
            double? ttlValue = ttl.HasValue ? ttl.Value.TotalMilliseconds : null;
            
            switch (type)
            {
                case RedisType.String:
                    var stringValue = await db.StringGetAsync(key);
                    result[keyString] = new RedisKeyData
                    {
                        Type = "string",
                        Value = stringValue.ToString(),
                        TTL = ttlValue
                    };
                    break;
                    
                case RedisType.List:
                    var listValues = await db.ListRangeAsync(key);
                    result[keyString] = new RedisKeyData
                    {
                        Type = "list",
                        Value = listValues.Select(v => v.ToString()).ToArray(),
                        TTL = ttlValue
                    };
                    break;
                    
                case RedisType.Set:
                    var setMembers = await db.SetMembersAsync(key);
                    result[keyString] = new RedisKeyData
                    {
                        Type = "set",
                        Value = setMembers.Select(v => v.ToString()).ToArray(),
                        TTL = ttlValue
                    };
                    break;
                    
                case RedisType.SortedSet:
                    var sortedSetEntries = await db.SortedSetRangeByScoreWithScoresAsync(key);
                    result[keyString] = new RedisKeyData
                    {
                        Type = "zset",
                        Value = sortedSetEntries.Select(v => new { Member = v.Element.ToString(), Score = v.Score }).ToArray(),
                        TTL = ttlValue
                    };
                    break;
                    
                case RedisType.Hash:
                    var hashEntries = await db.HashGetAllAsync(key);
                    result[keyString] = new RedisKeyData
                    {
                        Type = "hash",
                        Value = hashEntries.ToDictionary(he => he.Name.ToString(), he => he.Value.ToString()),
                        TTL = ttlValue
                    };
                    break;
                    
                default:
                    if (verbose)
                    {
                        AnsiConsole.MarkupLine($"[grey]Skipping key {keyString} of unsupported type {type}[/]");
                    }
                    break;
            }
        }
        
        return result;
    }
} 