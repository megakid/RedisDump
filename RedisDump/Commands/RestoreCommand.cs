using System.ComponentModel;
using System.Text.Json;
using Spectre.Console;
using Spectre.Console.Cli;
using StackExchange.Redis;

namespace RedisDump.Commands;

public class RestoreCommandSettings : SharedSettings
{
    [CommandOption("-i|--input <FILE>")]
    [Description("Input file path")]
    [DefaultValue("redis-dump.json")]
    public string InputFile { get; set; } = "redis-dump.json";
    
    [CommandOption("--flush")]
    [Description("Flush the database before restoring")]
    public bool Flush { get; set; }
}

public class RestoreCommand : AsyncCommand<RestoreCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, RestoreCommandSettings settings)
    {
        AnsiConsole.MarkupLine("[bold blue]Redis Restore Tool[/]");
        
        try
        {
            // Check if file exists
            if (!File.Exists(settings.InputFile))
            {
                AnsiConsole.MarkupLine($"[bold red]Error:[/] File {settings.InputFile} does not exist");
                return 1;
            }
            
            // Read file
            AnsiConsole.MarkupLine($"[yellow]Reading from {settings.InputFile}...[/]");
            var jsonString = await File.ReadAllTextAsync(settings.InputFile);
            var data = JsonSerializer.Deserialize<Dictionary<int, Dictionary<string, JsonElement>>>(jsonString);
            
            if (data == null || data.Count == 0)
            {
                AnsiConsole.MarkupLine("[bold red]Error:[/] No data found in file or invalid format");
                return 1;
            }
            
            // Build connection string with password if provided
            var connectionString = settings.ConnectionString;
            if (!string.IsNullOrEmpty(settings.Password))
            {
                connectionString = $"{connectionString},password={settings.Password}";
            }
            
            // Connect to Redis
            AnsiConsole.MarkupLine($"[yellow]Connecting to Redis at {settings.ConnectionString}...[/]");
            var connection = await ConnectionMultiplexer.ConnectAsync(connectionString);
            
            // Process the databases
            var totalKeysRestored = 0;
            
            await AnsiConsole.Progress()
                .StartAsync(async ctx =>
                {
                    var progressTask = ctx.AddTask("[green]Restoring databases[/]");
                    progressTask.MaxValue = data.Count;
                    
                    foreach (var dbEntry in data)
                    {
                        var dbNumber = dbEntry.Key;
                        var dbData = dbEntry.Value;
                        
                        // Filter databases if needed
                        if (settings.Database.HasValue && dbNumber != settings.Database.Value)
                        {
                            progressTask.Increment(1);
                            continue;
                        }
                        
                        var keysTask = ctx.AddTask($"[green]DB {dbNumber}: Restoring keys[/]");
                        keysTask.MaxValue = dbData.Count;
                        
                        var keysRestored = await RestoreDatabaseAsync(connection, dbNumber, dbData, settings.Flush, 
                            verbose: settings.Verbose, progressTask: keysTask);
                        
                        totalKeysRestored += keysRestored;
                        progressTask.Increment(1);
                    }
                });
            
            AnsiConsole.MarkupLine($"[green]Successfully restored {totalKeysRestored} keys[/]");
            
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
    
    private async Task<int> RestoreDatabaseAsync(
        ConnectionMultiplexer connection, 
        int databaseNumber, 
        Dictionary<string, JsonElement> data, 
        bool flush,
        bool verbose,
        ProgressTask progressTask)
    {
        var db = connection.GetDatabase(databaseNumber);
        var restoredCount = 0;
        
        if (verbose)
        {
            AnsiConsole.MarkupLine($"[grey]Restoring {data.Count} keys to database {databaseNumber}[/]");
        }
        
        // Flush database if requested
        if (flush)
        {
            if (verbose)
            {
                AnsiConsole.MarkupLine($"[grey]Flushing database {databaseNumber}[/]");
            }
            
            var server = connection.GetServer(connection.GetEndPoints().First());
            await server.FlushDatabaseAsync(databaseNumber);
        }
        
        // Use a transaction for better performance
        var batch = db.CreateBatch();
        var pendingTasks = new List<Task>();
        
        foreach (var kvp in data)
        {
            var key = kvp.Key;
            var jsonData = kvp.Value;
            
            try
            {
                // Get type of data
                string type = jsonData.GetProperty("Type").GetString() ?? "string";
                TimeSpan? ttl = null;
                
                // Check if TTL exists
                if (jsonData.TryGetProperty("TTL", out JsonElement ttlProperty)
                    && ttlProperty.ValueKind == JsonValueKind.Number
                    && ttlProperty.TryGetDouble(out var ttlValue))
                {
                    ttl = TimeSpan.FromMilliseconds(ttlValue);
                }
                
                switch (type.ToLowerInvariant())
                {
                    case "string":
                        {
                            var value = jsonData.GetProperty("Value").GetString() ?? string.Empty;
                            var task = ttl.HasValue 
                                ? batch.StringSetAsync(key, value, ttl) 
                                : batch.StringSetAsync(key, value);
                            pendingTasks.Add(task);
                            break;
                        }
                    
                    case "list":
                        {
                            var values = jsonData.GetProperty("Value").EnumerateArray()
                                .Select(v => (RedisValue)v.GetString())
                                .ToArray();
                            
                            // Delete existing key first
                            pendingTasks.Add(batch.KeyDeleteAsync(key));
                            
                            if (values.Length > 0)
                            {
                                pendingTasks.Add(batch.ListRightPushAsync(key, values));
                                
                                if (ttl.HasValue)
                                {
                                    pendingTasks.Add(batch.KeyExpireAsync(key, ttl));
                                }
                            }
                            break;
                        }
                    
                    case "set":
                        {
                            var values = jsonData.GetProperty("Value").EnumerateArray()
                                .Select(v => (RedisValue)v.GetString())
                                .ToArray();
                            
                            // Delete existing key first
                            pendingTasks.Add(batch.KeyDeleteAsync(key));
                            
                            if (values.Length > 0)
                            {
                                pendingTasks.Add(batch.SetAddAsync(key, values));
                                
                                if (ttl.HasValue)
                                {
                                    pendingTasks.Add(batch.KeyExpireAsync(key, ttl));
                                }
                            }
                            break;
                        }
                    
                    case "zset":
                        {
                            var entries = jsonData.GetProperty("Value").EnumerateArray()
                                .Select(v => new SortedSetEntry(
                                    v.GetProperty("Member").GetString(),
                                    v.GetProperty("Score").GetDouble()))
                                .ToArray();
                            
                            // Delete existing key first
                            pendingTasks.Add(batch.KeyDeleteAsync(key));
                            
                            if (entries.Length > 0)
                            {
                                pendingTasks.Add(batch.SortedSetAddAsync(key, entries));
                                
                                if (ttl.HasValue)
                                {
                                    pendingTasks.Add(batch.KeyExpireAsync(key, ttl));
                                }
                            }
                            break;
                        }
                    
                    case "hash":
                        {
                            // Delete existing key first
                            pendingTasks.Add(batch.KeyDeleteAsync(key));
                            
                            var entries = new List<HashEntry>();
                            
                            foreach (var hashElement in jsonData.GetProperty("Value").EnumerateObject())
                            {
                                entries.Add(new HashEntry(hashElement.Name, hashElement.Value.GetString()));
                            }
                            
                            if (entries.Count > 0)
                            {
                                pendingTasks.Add(batch.HashSetAsync(key, entries.ToArray()));
                                
                                if (ttl.HasValue)
                                {
                                    pendingTasks.Add(batch.KeyExpireAsync(key, ttl));
                                }
                            }
                            break;
                        }
                    
                    default:
                        if (verbose)
                        {
                            AnsiConsole.MarkupLine($"[grey]Skipping key {key} of unknown type {type}[/]");
                        }
                        break;
                }
                
                restoredCount++;
            }
            catch (Exception ex)
            {
                if (verbose)
                {
                    AnsiConsole.MarkupLine($"[grey]Error restoring key {key}: {ex.Message}[/]");
                }
            }
            
            progressTask.Increment(1);
        }
        
        // Execute the batch
        batch.Execute();
        
        // Wait for all tasks to complete
        await Task.WhenAll(pendingTasks);
        
        return restoredCount;
    }
} 